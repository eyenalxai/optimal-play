//! Perfect-information search over one draw phase round.
//!
//! A round is a DAG: every action either advances a deck position or moves a card,
//! and the opponent follows a deterministic policy. The solver therefore does a full
//! backward induction with memoization and returns the move with the best coin
//! outcome (win = opponent's bet, loss = own bet, tie = 0, sleeve costs are booked
//! into the own bet exactly like the game does).
//!
//! This is a 1:1 port of the managed solver that used to live in `Solver.cs`, so the
//! two implementations can be diffed move by move while the port settles.

use std::collections::HashMap;
use std::hash::{BuildHasherDefault, Hasher};
use std::time::Instant;

use crate::model::{Card, FLAG_BROKEN, State};
use crate::total::{highest, total};

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum MoveKind {
    Pass,
    PlayTop,
    SleeveTop,
    PlaySleeve,
}

#[derive(Clone, Copy, Debug)]
pub struct Move {
    pub kind: MoveKind,
    pub sleeve_index: i32,
    pub to_opponent: bool,
}

impl Move {
    pub fn pass() -> Self {
        Move {
            kind: MoveKind::Pass,
            sleeve_index: 0,
            to_opponent: false,
        }
    }

    pub fn play_top(to_opponent: bool) -> Self {
        Move {
            kind: MoveKind::PlayTop,
            sleeve_index: 0,
            to_opponent,
        }
    }

    pub fn sleeve_top() -> Self {
        Move {
            kind: MoveKind::SleeveTop,
            sleeve_index: 0,
            to_opponent: false,
        }
    }

    pub fn play_sleeve(index: usize, to_opponent: bool) -> Self {
        Move {
            kind: MoveKind::PlaySleeve,
            sleeve_index: index as i32,
            to_opponent,
        }
    }
}

#[derive(Clone, Copy)]
pub struct Eval {
    pub mv: Move,
    pub value: f32,
}

/// Why a root move got a heuristic nudge; surfaced in the plugin log.
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Note {
    None,
    ProgressTieBreak,
}

/// Why "sleeve top" is or is not a legal root move; the plugin turns this into text.
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum SleeveReason {
    Available,
    SizeZero,
    DrawPileEmpty,
    CannotPay,
    Unavailable,
}

#[derive(Clone, Copy)]
pub struct Budget {
    pub nodes: u64,
    pub time_ms: u64,
}

impl Budget {
    pub fn new(nodes: u64, time_ms: u64) -> Self {
        Budget {
            nodes: nodes.max(1),
            time_ms,
        }
    }
}

/// Everything the plugin needs to log a decision and execute it.
pub struct Outcome {
    pub best: Move,
    /// NaN when the search hit a budget and fell back to the greedy heuristic.
    pub value: f32,
    pub nodes: u64,
    pub aborted: bool,
    /// One entry per evaluated root move, in move order; truncated on abort.
    pub evaluations: Vec<Eval>,
    /// Legal root moves in move order (the evaluation list is its value prefix).
    pub legal: Vec<Move>,
    pub note: Note,
    pub p_value: i32,
    pub o_value: i32,
    pub p_target: i32,
    pub o_target: i32,
    pub payable: i32,
    pub sleeve_cost: i32,
    pub can_pass: bool,
    pub can_sleeve: bool,
    pub sleeve_reason: SleeveReason,
    pub top_null: bool,
    pub own_table_full: bool,
    pub opponent_table_full: bool,
}

/// Two-lane mixer for memo keys. 128 bits keep the hash map exact in practice
/// (a collision needs a 2^-128 event for distinct positions) while staying tiny.
#[derive(Clone, Copy)]
struct Hash128 {
    a: u64,
    b: u64,
}

impl Hash128 {
    fn new() -> Self {
        Hash128 {
            a: 0x243F_6A88_85A3_08D3,
            b: 0x1319_8A2E_0370_7344,
        }
    }

    fn mix(&mut self, x: u64) {
        self.a = (self.a ^ x).wrapping_mul(0x9E37_79B9_7F4A_7C15);
        self.a ^= self.a >> 32;
        self.b = (self.b.rotate_left(27) ^ self.a).wrapping_mul(0xC2B2_AE3D_27D4_EB4F);
        self.b ^= self.b >> 29;
    }

    fn finish(self) -> u128 {
        ((self.a as u128) << 64) | self.b as u128
    }
}

/// Hasher for the already-mixed `u128` keys; std's default SipHash is wasted work here.
#[derive(Default)]
struct U128Hasher(u64);

impl Hasher for U128Hasher {
    fn finish(&self) -> u64 {
        self.0
    }

    fn write(&mut self, bytes: &[u8]) {
        for &byte in bytes {
            self.0 = (self.0 ^ byte as u64).wrapping_mul(0x0000_0100_0000_01B3);
        }
    }

    fn write_u128(&mut self, value: u128) {
        self.0 ^= value as u64;
        self.0 = self.0.wrapping_mul(0x9E37_79B9_7F4A_7C15);
        self.0 ^= (value >> 64) as u64;
        self.0 = self.0.rotate_left(31).wrapping_mul(0xC2B2_AE3D_27D4_EB4F);
        self.0 ^= self.0 >> 29;
    }
}

type MemoMap = HashMap<u128, f32, BuildHasherDefault<U128Hasher>>;

/// Memo key of a position in a given game phase. Only the fields that can differ
/// between two positions reached in the same search are part of it: the round
/// constants (targets, rules, sleeve costs) and the discard contents never change
/// while a search runs, and decks never reshuffle inside the tree.
fn memo_key(phase: u8, s: &State) -> u128 {
    let mut h = Hash128::new();
    h.mix(phase as u64);
    hash_side(&mut h, &s.p);
    hash_side(&mut h, &s.o);
    h.mix(s.insight_left as u64);
    h.mix(s.payable as u64);
    h.finish()
}

fn hash_side(h: &mut Hash128, side: &crate::model::Side) {
    h.mix(side.deck_pos as u64);
    h.mix(side.passed as u64);
    h.mix(side.out_of_cards as u64);
    h.mix(side.capacity as u64);
    h.mix(side.sleeve_draws as u64);
    h.mix(side.bet as u64);
    h.mix(side.sleeve.len() as u64);
    for card in &side.sleeve {
        hash_card(h, card);
    }
    // Tables are multisets: order never affects the game, so sort for a canonical key.
    let mut table: Vec<&Card> = side.table.iter().collect();
    table.sort_unstable();
    h.mix(table.len() as u64);
    for card in table {
        hash_card(h, card);
    }
}

fn hash_card(h: &mut Hash128, card: &Card) {
    h.mix(card.value_count as u64);
    for &value in card.values() {
        h.mix(value as u64);
    }
    // The "broken" flag is derived from the values, so hash the derivation rather than
    // trusting the transport bit (the managed Sig does the same).
    h.mix((card.flags & !FLAG_BROKEN) as u64);
    h.mix(card.values().iter().any(|&value| value < 0) as u64);
}

struct Solver {
    node_budget: u64,
    time_budget_ms: u64,
    memo: MemoMap,
    nodes: u64,
    aborted: bool,
    start: Instant,
}

/// Search the round and return the best root move plus its evaluation.
pub fn solve(root: &State, budget: Budget) -> Outcome {
    Solver::new(budget).best_move(root)
}

/// Score a position exactly as the game loop after the player's action would play out.
/// Takes ownership because the game loop mutates the state while running it (the
/// managed solver did the same; callers pass clones).
pub fn evaluate(root: State, budget: Budget) -> f32 {
    Solver::new(budget).after_player_action(root)
}

impl Solver {
    fn new(budget: Budget) -> Self {
        Solver {
            node_budget: budget.nodes,
            time_budget_ms: budget.time_ms,
            memo: MemoMap::default(),
            nodes: 0,
            aborted: false,
            start: Instant::now(),
        }
    }

    fn best_move(&mut self, root: &State) -> Outcome {
        let mut best = Move::pass();
        let mut best_value = f32::NEG_INFINITY;
        let mut evaluations = Vec::new();
        let mut legal = Vec::new();

        for (mv, state) in enumerate_moves(root) {
            legal.push(mv);
            let value = if mv.kind == MoveKind::SleeveTop {
                self.best_player(&state)
            } else {
                self.after_player_action(state)
            };
            evaluations.push(Eval { mv, value });
            if value > best_value {
                best_value = value;
                best = mv;
            }
            if self.aborted {
                break;
            }
        }

        let mut value = best_value;
        let mut note = Note::None;
        if self.aborted || evaluations.is_empty() {
            // Out of budget: answer with the cheap heuristic instead of a half search.
            // Use the same NaN bits as .NET's float.NaN so bit-level comparisons hold.
            best = greedy_move(root);
            value = f32::from_bits(0xFFC0_0000);
        } else if best.kind == MoveKind::Pass && best_value <= 0.0001 {
            // Losing a round costs the bet no matter how it is lost; cycle a dead card
            // instead of passing and freezing the deck on it forever.
            let progress = progress_move(root, &evaluations, best_value);
            if progress.kind != MoveKind::Pass {
                best = progress;
                note = Note::ProgressTieBreak;
            }
        }

        let can_sleeve = can_sleeve(root);
        Outcome {
            best,
            value,
            nodes: self.nodes,
            aborted: self.aborted,
            evaluations,
            legal,
            note,
            p_value: root.p_value(),
            o_value: root.o_value(),
            p_target: root.p_target(),
            o_target: root.o_target(),
            payable: root.payable,
            sleeve_cost: root.sleeve_cost(),
            can_pass: can_pass(root),
            can_sleeve,
            sleeve_reason: sleeve_reason(root, can_sleeve),
            top_null: root.p.top().is_none(),
            own_table_full: root.p.capacity <= 0,
            opponent_table_full: root
                .p
                .top()
                .map(|top| top.can_play_opponent())
                .unwrap_or(false)
                && root.o.capacity <= 0,
        }
    }

    /// Best value for the player when it is the player's turn to choose.
    fn best_player(&mut self, s: &State) -> f32 {
        let key = memo_key(b'P', s);
        if let Some(cached) = self.memo.get(&key) {
            return *cached;
        }
        if self.budget_exceeded() {
            return resolve(s);
        }

        let mut best = f32::NEG_INFINITY;
        for (mv, child) in enumerate_moves(s) {
            let value = if mv.kind == MoveKind::SleeveTop {
                self.best_player(&child)
            } else {
                self.after_player_action(child)
            };
            if value > best {
                best = value;
            }
            if self.aborted {
                return resolve(s);
            }
        }
        if best == f32::NEG_INFINITY {
            best = resolve(s);
        }
        self.memo.insert(key, best);
        best
    }

    /// Continuation right after the player's turn ended (card played or passed):
    /// the opponent unpass check runs, then the game loop resumes.
    fn after_player_action(&mut self, mut s: State) -> f32 {
        let key = memo_key(b'A', &s);
        if let Some(cached) = self.memo.get(&key) {
            return *cached;
        }
        if self.budget_exceeded() {
            return resolve(&s);
        }

        opponent_unpass_check(&mut s);
        if s.p.passed && s.o.passed {
            // Reveal face-down cards and check once more.
            opponent_unpass_check(&mut s);
            if s.o.passed {
                let resolved = resolve(&s);
                self.memo.insert(key, resolved);
                return resolved;
            }
        }

        let result = self.run(&mut s);
        self.memo.insert(key, result);
        result
    }

    /// Game loop: opponent turns and resolutions until the player has to choose again.
    fn run(&mut self, s: &mut State) -> f32 {
        let key = memo_key(b'R', s);
        if let Some(cached) = self.memo.get(&key) {
            return *cached;
        }
        if self.budget_exceeded() {
            return resolve(s);
        }

        let result;
        loop {
            if s.p.passed && s.o.passed {
                result = resolve(s);
                break;
            }

            if !s.o.passed {
                opponent_act(s);
                opponent_unpass_check(s);
            }

            if !s.p.passed {
                result = self.best_player(s);
                break;
            }

            // Player already passed: finish the loop body like the game does.
            opponent_unpass_check(s);
            if s.p.passed && s.o.passed {
                opponent_unpass_check(s);
                if s.o.passed {
                    result = resolve(s);
                    break;
                }
            }
        }

        self.memo.insert(key, result);
        result
    }

    fn budget_exceeded(&mut self) -> bool {
        if self.aborted {
            return true;
        }
        self.nodes += 1;
        if self.nodes > self.node_budget {
            self.aborted = true;
            return true;
        }
        if self.nodes & 1023 == 0 && self.start.elapsed().as_millis() as u64 > self.time_budget_ms {
            self.aborted = true;
            return true;
        }
        false
    }
}

// ---------------------------------------------------------------- rules

/// Every legal root move with the state it leads to, in the order the plugin lists them.
fn enumerate_moves(s: &State) -> Vec<(Move, State)> {
    let mut moves = Vec::new();

    if can_pass(s) {
        let mut n = s.clone();
        n.p.passed = true;
        moves.push((Move::pass(), n));
    }

    let top = s.p.top().copied();
    if let Some(top) = top {
        if s.p.capacity > 0 {
            let mut n = s.clone();
            n.p.deck_pos += 1;
            n.p.table.push(top);
            n.p.capacity -= 1;
            if top.is_hollow() {
                n.p.capacity += 1;
            }
            n.invalidate_values();
            moves.push((Move::play_top(false), n));
        }
        if top.can_play_opponent() && s.o.capacity > 0 {
            let mut n = s.clone();
            n.p.deck_pos += 1;
            n.o.table.push(top);
            n.o.capacity -= 1;
            if top.is_hollow() {
                n.o.capacity += 1;
            }
            n.invalidate_values();
            moves.push((Move::play_top(true), n));
        }
    }

    if s.p.capacity > 0 || s.o.capacity > 0 {
        for (index, card) in s.p.sleeve.iter().enumerate() {
            if s.p.capacity > 0 {
                let mut n = s.clone();
                n.p.sleeve.remove(index);
                n.p.table.push(*card);
                n.p.capacity -= 1;
                if card.is_hollow() {
                    n.p.capacity += 1;
                }
                n.invalidate_values();
                moves.push((Move::play_sleeve(index, false), n));
            }
            if card.can_play_opponent() && s.o.capacity > 0 {
                let mut n = s.clone();
                n.p.sleeve.remove(index);
                n.o.table.push(*card);
                n.o.capacity -= 1;
                if card.is_hollow() {
                    n.o.capacity += 1;
                }
                n.invalidate_values();
                moves.push((Move::play_sleeve(index, true), n));
            }
        }
    }

    if can_sleeve(s) {
        let top = *s.p.top().expect("can_sleeve checked the draw pile");
        let cost = s.sleeve_cost();
        let mut n = s.clone();
        n.p.deck_pos += 1;
        let max = s.sleeve_size.max(1) as usize;
        if n.p.sleeve.len() >= max {
            n.p.sleeve.remove(0);
        }
        n.p.sleeve.push(top);
        n.p.sleeve_draws += 1;
        n.p.bet += cost;
        n.payable -= cost;
        moves.push((Move::sleeve_top(), n));
    }

    moves
}

fn can_pass(s: &State) -> bool {
    if s.supper {
        // Supper: passing is only allowed when the table has no free slot.
        return s.p.capacity <= 0;
    }
    true
}

fn can_sleeve(s: &State) -> bool {
    s.sleeve_size > 0
        && s.p.top().is_some()
        && !s.sleeve_costs.is_empty()
        && s.payable >= s.sleeve_cost()
}

fn sleeve_reason(s: &State, can_sleeve: bool) -> SleeveReason {
    if can_sleeve {
        return SleeveReason::Available;
    }
    if s.sleeve_size <= 0 {
        SleeveReason::SizeZero
    } else if s.p.top().is_none() {
        SleeveReason::DrawPileEmpty
    } else if s.payable < s.sleeve_cost() {
        SleeveReason::CannotPay
    } else {
        SleeveReason::Unavailable
    }
}

// ---------------------------------------------------------------- opponent policy

fn opponent_act(s: &mut State) {
    if s.o.top().is_none() {
        // The game would reshuffle the discard pile; the new order is random, so the
        // deterministic search stops here and treats the opponent as standing.
        s.o.out_of_cards = true;
        s.o.passed = true;
        return;
    }

    if !opp_will_draw(s) {
        s.o.passed = true;
        return;
    }

    let card = *s.o.top().expect("checked above");
    s.o.deck_pos += 1;
    s.o.table.push(card);
    s.o.capacity -= 1;
    if card.is_hollow() {
        s.o.capacity += 1;
    }
    s.invalidate_values();
    s.insight_left = (s.insight_left - 1).max(0);
}

fn opp_will_draw(s: &State) -> bool {
    let o = &s.o;
    if o.out_of_cards {
        return false;
    }
    if !o.has_drawable_cards() {
        return false;
    }
    if s.o_bj() || s.o_value() == s.o_target() {
        return false;
    }
    if s.p_value() > s.p_target() {
        return false;
    }
    if s.p.passed && s.p_value() < s.o_value() {
        return false;
    }

    let peek = o.top();
    if o.capacity <= 0 {
        return false;
    }

    if let Some(peek) = peek
        && (s.insight_left > 0 || peek.always_insight())
    {
        let mut table = o.table.clone();
        table.push(*peek);
        let raw = total(&table, s.o_target());
        if raw <= s.o_target() && raw + s.o_mods >= s.o_value() {
            return true;
        }
    }

    s.o_value() < s.holds_at
}

fn opponent_unpass_check(s: &mut State) {
    if s.o.passed && opp_will_draw(s) {
        s.o.passed = false;
    }
}

// ---------------------------------------------------------------- resolution

// The branches mirror the game's settle order one to one; adjacent branches set the
// same winner for different reasons on purpose.
#[allow(clippy::if_same_then_else)]
fn resolve(s: &State) -> f32 {
    let mut winner = 0i32;
    let p_bj = s.p_bj();
    let o_bj = s.o_bj();

    if p_bj && !o_bj {
        winner = 1;
    } else if o_bj && !p_bj {
        winner = -1;
    } else if p_bj && o_bj {
        winner = 0;
    } else {
        let pv = s.p_value();
        let ov = s.o_value();
        if s.p_busted() && s.o_busted() {
            winner = 0;
        } else if pv > s.plain_target {
            winner = -1;
        } else if ov > s.plain_target {
            winner = 1;
        } else if pv > ov {
            winner = 1;
        } else if ov > pv {
            winner = -1;
        } else {
            let ph = highest(&s.p.table);
            let oh = highest(&s.o.table);
            if ph > oh {
                winner = 1;
            } else if oh > ph {
                winner = -1;
            }
        }
    }

    if winner > 0 {
        s.o.bet as f32
    } else if winner < 0 {
        // Negate the integer first: a zero bet must produce +0.0, not -0.0, to match
        // the managed solver's float bits exactly.
        (-s.p.bet) as f32
    } else {
        0.0
    }
}

/// Quick heuristic used when the search had to abort (or the root has no move).
fn greedy_move(s: &State) -> Move {
    if s.o_busted() && !s.p_busted() {
        return Move::pass();
    }
    if s.o.passed && s.p_value() > s.o_value() {
        return Move::pass();
    }

    if let Some(top) = s.p.top()
        && s.p.capacity > 0
    {
        let mut table = s.p.table.clone();
        table.push(*top);
        let value = total(&table, s.p_target()) + s.p_mods;
        if value <= s.plain_target && (value > s.p_value() || s.o_value() > s.p_value()) {
            return Move::play_top(false);
        }
    }

    if can_sleeve(s) {
        return Move::sleeve_top();
    }

    if s.p.capacity > 0 {
        let mut best_index = -1i32;
        let mut best_value = i32::MIN;
        for (index, card) in s.p.sleeve.iter().enumerate() {
            let mut table = s.p.table.clone();
            table.push(*card);
            let value = total(&table, s.p_target()) + s.p_mods;
            if value <= s.plain_target && value > best_value {
                best_value = value;
                best_index = index as i32;
            }
        }
        if best_index >= 0 {
            return Move::play_sleeve(best_index as usize, false);
        }
    }

    Move::pass()
}

/// Deck-progress nudge: among the moves that tie the best (non-positive) value, prefer
/// playing a dead card over passing, so the deck keeps moving.
fn progress_move(root: &State, evaluations: &[Eval], best: f32) -> Move {
    const EPS: f32 = 0.0001;
    let mut sleeve = None;
    let mut has_sleeve = false;

    for eval in evaluations {
        if eval.value < best - EPS {
            continue;
        }
        match eval.mv.kind {
            MoveKind::PlayTop => {
                if let Some(top) = root.p.top()
                    && top.is_dead()
                {
                    return eval.mv;
                }
            }
            MoveKind::PlaySleeve => {
                let index = eval.mv.sleeve_index;
                if !has_sleeve
                    && index >= 0
                    && let Some(card) = root.p.sleeve.get(index as usize)
                    && card.is_dead()
                {
                    sleeve = Some(eval.mv);
                    has_sleeve = true;
                }
            }
            _ => {}
        }
    }

    sleeve.unwrap_or_else(Move::pass)
}
