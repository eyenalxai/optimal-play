//! Perfect-information search over one draw phase round.
//!
//! A round is a DAG *unless a card effect creates cards or shuttles them between a
//! sleeve and a table*: a hollow card that duplicates itself back into the sleeve can be
//! played forever, so the game tree is not guaranteed to be finite. The search therefore
//! bounds every line by recursion depth and by actions that made no deck progress; when a
//! cap is hit the line is scored with [`resolve`], which is the same horizon the node and
//! time budgets use. Legitimate rounds finish far below both caps.
//!
//! This is a 1:1 port of the managed solver that used to live in `Solver.cs`, so the
//! two implementations can be diffed move by move while the port settles.

use std::collections::HashMap;
use std::hash::{BuildHasherDefault, Hasher};
use std::time::Instant;

use smallvec::SmallVec;

use crate::model::{Card, State, TRIGGER_RESOLUTION_AFTER, TRIGGER_RESOLUTION_BEFORE};
use crate::total::{highest, total};
use crate::{effects, model};

/// Longest line the search follows, in player decisions. Beyond this the state is scored
/// at the horizon. Only effect loops that keep creating table cards can reach it.
const MAX_DEPTH: u32 = 2048;
/// Player decisions allowed without the draw pile of either side advancing. A sleeve
/// card that keeps returning to the sleeve would otherwise recurse forever; a real round
/// cannot stall this long, since sleeves hold a handful of cards.
const MAX_STALL: u32 = 96;

/// Per-line budget carried through the recursion: `depth` counts player decisions,
/// `stall` counts decisions since either deck position last moved.
#[derive(Clone, Copy)]
struct Ply {
    depth: u32,
    stall: u32,
}

impl Ply {
    const ROOT: Ply = Ply { depth: 0, stall: 0 };

    /// The next player decision, reached by applying a move to `parent`.
    fn next(self, parent: &State, child: &State) -> Ply {
        let advanced =
            child.p.deck_pos != parent.p.deck_pos || child.o.deck_pos != parent.o.deck_pos;
        Ply {
            depth: self.depth + 1,
            stall: if advanced { 0 } else { self.stall + 1 },
        }
    }

    /// The next player decision after the opponent ran its policy.
    fn after_opponent(self, advanced: bool) -> Ply {
        Ply {
            depth: self.depth + 1,
            stall: if advanced { 0 } else { self.stall + 1 },
        }
    }

    fn cut(self) -> bool {
        self.depth >= MAX_DEPTH || self.stall >= MAX_STALL
    }
}

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
    /// Index into [`Outcome::legal`]; evaluations are not necessarily a prefix any more.
    pub index: usize,
    pub mv: Move,
    pub value: f32,
}

/// Heuristic nudges and search-limit flags of a root result; surfaced in the plugin log.
#[derive(Clone, Copy, Default, PartialEq, Eq)]
pub struct Note {
    /// The chosen move was swapped for the progress tie-break (a play over a pass).
    pub progress_tie_break: bool,
    /// An effect loop forced the search to score a line at the depth or stall cap;
    /// values below that point are horizon estimates.
    pub depth_capped: bool,
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

/// One action of the expected line of play behind the chosen move, plus the position
/// right after it. The numbers make effect-driven changes visible in the plugin log:
/// card effects can move coins between stash and bet or change table values without a
/// corresponding player move.
#[derive(Clone, Copy)]
pub struct TraceStep {
    pub action: TraceAction,
    pub p_value: i32,
    pub o_value: i32,
    pub p_bet: i32,
    pub o_bet: i32,
    pub p_stash: i32,
    pub o_stash: i32,
}

impl TraceStep {
    fn after(action: TraceAction, s: &State) -> Self {
        TraceStep {
            action,
            p_value: s.p_value(),
            o_value: s.o_value(),
            p_bet: s.p.bet,
            o_bet: s.o.bet,
            p_stash: s.p.stash,
            o_stash: s.o.stash,
        }
    }
}

#[derive(Clone, Copy)]
pub enum TraceAction {
    Player { mv: Move, card: Option<Card> },
    OpponentDraw { card: Card },
    OpponentPass,
    Resolve { winner: i32 },
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
    /// True when the search ran out of budget before evaluating every root move; `best`
    /// and `value` are then the best *completed* root move instead of a greedy guess.
    pub partial: bool,
    /// One entry per evaluated root move (canonical order); incomplete on abort.
    pub evaluations: Vec<Eval>,
    /// Legal root moves in canonical order.
    pub legal: Vec<Move>,
    /// Expected line of play after `best`, when the search completed its root moves.
    pub trace: Vec<TraceStep>,
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

/// Chosen move per player-decision position, packed for the principal-variation replay.
type ChoiceMap = HashMap<u128, u32, BuildHasherDefault<U128Hasher>>;

fn pack_move(mv: Move) -> u32 {
    let kind = match mv.kind {
        MoveKind::Pass => 0,
        MoveKind::PlayTop => 1,
        MoveKind::SleeveTop => 2,
        MoveKind::PlaySleeve => 3,
    };
    kind | ((mv.sleeve_index.max(0) as u32 & 0x3F) << 2) | (u32::from(mv.to_opponent) << 8)
}

fn unpack_move(packed: u32) -> Move {
    let kind = match packed & 3 {
        1 => MoveKind::PlayTop,
        2 => MoveKind::SleeveTop,
        3 => MoveKind::PlaySleeve,
        _ => MoveKind::Pass,
    };
    Move {
        kind,
        sleeve_index: ((packed >> 2) & 0x3F) as i32,
        to_opponent: packed & (1 << 8) != 0,
    }
}

/// Memo key of a position in a given game phase. Only the fields that can differ
/// between two positions reached in the same search are part of it: the round
/// constants (targets, rules, sleeve costs) and the discard contents never change
/// while a search runs, and decks never reshuffle inside the tree.
fn memo_key(phase: u8, s: &State) -> u128 {
    let mut h = Hash128::new();
    h.mix(phase as u64);
    // Effect-free positions never change a deck's order, so the deck contents are fixed
    // for the whole search and hashing the position is enough. With effects in play a
    // card can be moved into or out of a deck, so the untouched tail must key the state.
    let full_deck = !s.effect_blocks.is_empty();
    hash_side(&mut h, &s.p, full_deck);
    hash_side(&mut h, &s.o, full_deck);
    h.mix(s.insight_left as u64);
    h.mix(s.payable as u64);
    h.mix(s.pot as u64);
    h.finish()
}

fn hash_side(h: &mut Hash128, side: &crate::model::Side, full_deck: bool) {
    h.mix(side.deck_pos as u64);
    if full_deck {
        let tail = &side.deck[(side.deck_pos as usize).min(side.deck.len())..];
        h.mix(tail.len() as u64);
        for card in tail {
            hash_card(h, card);
        }
    }
    h.mix(side.passed as u64);
    h.mix(side.out_of_cards as u64);
    h.mix(side.capacity as u64);
    h.mix(side.sleeve_draws as u64);
    h.mix(side.bet as u64);
    h.mix(side.stash as u64);
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
    let count = card.value_count as usize;
    h.mix(count as u64);
    // Two values per mix; the low half of the final odd value is zero-extended, so equal
    // packs imply equal values and the sign survives in the two's complement bits.
    let values = &card.values[..count];
    let mut index = 0;
    while index + 1 < count {
        let low = values[index] as u32 as u64;
        let high = values[index + 1] as u32 as u64;
        h.mix(low | (high << 32));
        index += 2;
    }
    if index < count {
        h.mix(values[index] as u32 as u64);
    }
    // All value types fit in one word (at most eight bytes).
    let mut types = 0u64;
    for (shift, &value_type) in card.types[..count].iter().enumerate() {
        types |= (value_type as u64) << (shift * 8);
    }
    h.mix(types);
    // Broken-ness is carried by the value types; the legacy flag bit is derived state.
    h.mix((card.flags & !model::FLAG_BROKEN) as u64);
    // Two cards with different ignite counts can behave differently at resolution.
    h.mix(card.ignited as u64);
    // Effect blocks are interned per state, so the reference identifies the whole list;
    // two cards with equal values but different effects must hash differently.
    h.mix(card.effects_ref as u64);
}

struct Solver {
    node_budget: u64,
    time_budget_ms: u64,
    memo: MemoMap,
    choices: ChoiceMap,
    nodes: u64,
    aborted: bool,
    /// Set when a line was scored at the depth or stall cap; surfaced as a note.
    depth_capped: bool,
    start: Instant,
}

/// Search the round and return the best root move plus its evaluation.
pub fn solve(root: &State, budget: Budget) -> Outcome {
    let mut solver = Solver::new(budget);
    let mut outcome = solver.best_move(root);
    if !outcome.aborted || outcome.partial {
        outcome.trace = build_trace(root, outcome.best, &solver.choices);
    }
    outcome
}

/// Score a position exactly as the game loop after the player's action would play out.
/// Takes ownership because the game loop mutates the state while running it (the
/// managed solver did the same; callers pass clones).
pub fn evaluate(root: State, budget: Budget) -> f32 {
    Solver::new(budget).after_player_action(root, Ply::ROOT)
}

impl Solver {
    fn new(budget: Budget) -> Self {
        Solver {
            node_budget: budget.nodes,
            time_budget_ms: budget.time_ms,
            memo: MemoMap::default(),
            choices: ChoiceMap::default(),
            nodes: 0,
            aborted: false,
            depth_capped: false,
            start: Instant::now(),
        }
    }

    fn best_move(&mut self, root: &State) -> Outcome {
        let moves = candidate_moves(root);
        let legal: Vec<Move> = moves.to_vec();
        let mut values: Vec<Option<f32>> = vec![None; moves.len()];
        let mut best_index: Option<usize> = None;

        // Evaluate root moves in preference order so that a budget abort has already
        // covered the plausible candidates; ties still resolve to the earliest canonical
        // move, so a complete search reports exactly the move a canonical scan would.
        for index in preference_order(root, &moves) {
            let mv = moves[index];
            let (child, _) =
                apply_player_move(root, mv).expect("candidate_moves only lists legal moves");
            let ply = Ply::ROOT.next(root, &child);
            let value = if mv.kind == MoveKind::SleeveTop {
                self.best_player(&child, ply)
            } else {
                self.after_player_action(child, ply)
            };
            if self.aborted {
                // The value is a horizon estimate, not a finished search; do not trust it.
                break;
            }
            values[index] = Some(value);
            best_index = match best_index {
                None => Some(index),
                Some(current) => {
                    let current_value = values[current].expect("completed moves are stored");
                    if value > current_value || (value == current_value && index < current) {
                        Some(index)
                    } else {
                        Some(current)
                    }
                }
            };
        }

        let evaluations: Vec<Eval> = (0..moves.len())
            .filter_map(|index| {
                values[index].map(|value| Eval {
                    index,
                    mv: legal[index],
                    value,
                })
            })
            .collect();

        let mut note = Note {
            depth_capped: self.depth_capped,
            ..Note::default()
        };
        let partial = self.aborted && best_index.is_some();
        let (mut best, value) = match best_index {
            Some(index) => (
                legal[index],
                values[index].expect("completed moves are stored"),
            ),
            None => {
                // No legal move at all, or the budget ran out before one finished: answer
                // with the cheap heuristic. Use the same NaN bits as .NET's float.NaN.
                (greedy_move(root), f32::from_bits(0xFFC0_0000))
            }
        };

        if best_index.is_some() && best.kind == MoveKind::Pass && value <= 0.0001 {
            // Losing a round costs the bet no matter how it is lost; cycle a dead card
            // instead of passing and freezing the deck on it forever.
            let progress = progress_move(root, &evaluations, value);
            if progress.kind != MoveKind::Pass {
                best = progress;
                note.progress_tie_break = true;
            }
        }

        let can_sleeve = can_sleeve(root);
        Outcome {
            best,
            value,
            nodes: self.nodes,
            aborted: self.aborted,
            partial,
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
            trace: Vec::new(),
        }
    }

    /// Best value for the player when it is the player's turn to choose.
    fn best_player(&mut self, s: &State, ply: Ply) -> f32 {
        if ply.cut() {
            self.depth_capped = true;
            return resolve(s);
        }
        let key = memo_key(b'P', s);
        if let Some(cached) = self.memo.get(&key) {
            return *cached;
        }
        if self.budget_exceeded() {
            return resolve(s);
        }

        let mut best = f32::NEG_INFINITY;
        let mut best_move = None;
        for mv in candidate_moves(s) {
            let (child, _) =
                apply_player_move(s, mv).expect("candidate_moves only lists legal moves");
            let child_ply = ply.next(s, &child);
            let value = if mv.kind == MoveKind::SleeveTop {
                self.best_player(&child, child_ply)
            } else {
                self.after_player_action(child, child_ply)
            };
            if value > best {
                best = value;
                best_move = Some(mv);
            }
            if self.aborted {
                return resolve(s);
            }
        }
        match best_move {
            Some(mv) => {
                self.choices.insert(key, pack_move(mv));
            }
            None => best = resolve(s),
        };
        self.memo.insert(key, best);
        best
    }

    /// Continuation right after the player's turn ended (card played or passed):
    /// the opponent unpass check runs, then the game loop resumes.
    fn after_player_action(&mut self, mut s: State, ply: Ply) -> f32 {
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

        let result = self.run(&mut s, ply);
        self.memo.insert(key, result);
        result
    }

    /// Game loop: opponent turns and resolutions until the player has to choose again.
    fn run(&mut self, s: &mut State, ply: Ply) -> f32 {
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

            let mut advanced = false;
            if !s.o.passed {
                let before = (s.p.deck_pos, s.o.deck_pos);
                opponent_act(s);
                advanced = (s.p.deck_pos, s.o.deck_pos) != before;
                opponent_unpass_check(s);
            }

            if !s.p.passed {
                result = self.best_player(s, ply.after_opponent(advanced));
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
        if self.nodes & 4095 == 0 && self.start.elapsed().as_millis() as u64 > self.time_budget_ms {
            self.aborted = true;
            return true;
        }
        false
    }
}

// ---------------------------------------------------------------- rules

/// Legal player moves in the canonical order the plugin reports them.
fn candidate_moves(s: &State) -> SmallVec<[Move; 16]> {
    let mut moves = SmallVec::new();

    if can_pass(s) {
        moves.push(Move::pass());
    }

    let top = s.p.top().copied();
    if let Some(top) = top {
        if s.p.capacity > 0 {
            moves.push(Move::play_top(false));
        }
        if top.can_play_opponent() && s.o.capacity > 0 {
            moves.push(Move::play_top(true));
        }
    }

    if s.p.capacity > 0 || s.o.capacity > 0 {
        for (index, card) in s.p.sleeve.iter().enumerate() {
            if s.p.capacity > 0 {
                moves.push(Move::play_sleeve(index, false));
            }
            if card.can_play_opponent() && s.o.capacity > 0 {
                moves.push(Move::play_sleeve(index, true));
            }
        }
    }

    if can_sleeve(s) {
        moves.push(Move::sleeve_top());
    }

    moves
}

/// Apply one legal player move, returning the new state and the card it acted on (if any).
/// This is the single place where playing, sleeving and passing change the state.
fn apply_player_move(s: &State, mv: Move) -> Option<(State, Option<Card>)> {
    match mv.kind {
        MoveKind::Pass => {
            let mut n = s.clone();
            n.p.passed = true;
            Some((n, None))
        }
        MoveKind::PlayTop => {
            let top = *s.p.top()?;
            let mut n = s.clone();
            n.p.deck_pos += 1;
            if mv.to_opponent {
                n.o.table.push(top);
                n.o.capacity -= 1;
                if top.is_hollow() {
                    n.o.capacity += 1;
                }
            } else {
                n.p.table.push(top);
                n.p.capacity -= 1;
                if top.is_hollow() {
                    n.p.capacity += 1;
                }
            }
            n.invalidate_values();
            effects::apply_play(&mut n, usize::from(mv.to_opponent));
            Some((n, Some(top)))
        }
        MoveKind::PlaySleeve => {
            let index = mv.sleeve_index.max(0) as usize;
            let card = *s.p.sleeve.get(index)?;
            let mut n = s.clone();
            n.p.sleeve.remove(index);
            if mv.to_opponent {
                n.o.table.push(card);
                n.o.capacity -= 1;
                if card.is_hollow() {
                    n.o.capacity += 1;
                }
            } else {
                n.p.table.push(card);
                n.p.capacity -= 1;
                if card.is_hollow() {
                    n.p.capacity += 1;
                }
            }
            n.invalidate_values();
            effects::apply_play(&mut n, usize::from(mv.to_opponent));
            Some((n, Some(card)))
        }
        MoveKind::SleeveTop => {
            if !can_sleeve(s) {
                return None;
            }
            let top = *s.p.top()?;
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
            Some((n, Some(top)))
        }
    }
}

/// Search root moves in decreasing order of promise, so a budget abort has already looked
/// at the heuristic's first choice and the most natural alternatives. The sort is stable,
/// so everything else keeps the canonical order used for tie-breaking.
fn preference_order(root: &State, moves: &[Move]) -> Vec<usize> {
    let greedy = greedy_move(root);
    let mut order: Vec<usize> = (0..moves.len()).collect();
    order.sort_by_key(|&index| {
        let mv = moves[index];
        let is_greedy = mv.kind == greedy.kind
            && mv.sleeve_index == greedy.sleeve_index
            && mv.to_opponent == greedy.to_opponent;
        u8::from(!is_greedy)
    });
    order
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

/// What the deterministic opponent policy did on its turn; the principal-variation replay
/// records it, the search ignores it. `Card` is a plain value type and the enum only lives
/// in the trace, so the variants stay inline instead of allocating.
#[allow(clippy::large_enum_variant)]
#[derive(Clone, Copy)]
enum OpponentAction {
    Draw(Card),
    Pass,
}

fn opponent_act(s: &mut State) -> OpponentAction {
    if s.o.top().is_none() {
        // The game would reshuffle the discard pile; the new order is random, so the
        // deterministic search stops here and treats the opponent as standing.
        s.o.out_of_cards = true;
        s.o.passed = true;
        return OpponentAction::Pass;
    }

    if !opp_will_draw(s) {
        s.o.passed = true;
        return OpponentAction::Pass;
    }

    let card = *s.o.top().expect("checked above");
    s.o.deck_pos += 1;
    s.o.table.push(card);
    s.o.capacity -= 1;
    if card.is_hollow() {
        s.o.capacity += 1;
    }
    s.invalidate_values();
    effects::apply_play(s, 1);
    s.insight_left = (s.insight_left - 1).max(0);
    OpponentAction::Draw(card)
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
fn winner(s: &State) -> i32 {
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
    winner
}

fn resolve(s: &State) -> f32 {
    match settled_state(s) {
        Some(resolved) => resolve_settled(&resolved),
        None => resolve_settled(s),
    }
}

/// Apply the resolution trigger sweep when any table card carries effects. Almost every
/// state has none, so the common path stays allocation-free.
fn settled_state(s: &State) -> Option<State> {
    if s.effect_blocks.is_empty() {
        return None;
    }
    let has_effects = |side: &crate::model::Side| side.table.iter().any(|card| card.has_effects());
    if !has_effects(&s.p) && !has_effects(&s.o) {
        return None;
    }
    let mut resolved = s.clone();
    effects::apply_trigger(&mut resolved, TRIGGER_RESOLUTION_BEFORE);
    effects::apply_trigger(&mut resolved, TRIGGER_RESOLUTION_AFTER);
    Some(resolved)
}

#[allow(clippy::if_same_then_else)]
fn resolve_settled(s: &State) -> f32 {
    match winner(s) {
        w if w > 0 => s.o.bet as f32,
        w if w < 0 => {
            // Negate the integer first: a zero bet must produce +0.0, not -0.0, to match
            // the managed solver's float bits exactly.
            (-s.p.bet) as f32
        }
        _ => 0.0,
    }
}

fn resolve_step(s: &State) -> TraceStep {
    match settled_state(s) {
        Some(resolved) => TraceStep::after(
            TraceAction::Resolve {
                winner: winner(&resolved),
            },
            &resolved,
        ),
        None => TraceStep::after(TraceAction::Resolve { winner: winner(s) }, s),
    }
}

/// Replay the expected line of play behind the chosen root move. Every player decision
/// follows the move the search chose for that position; the opponent follows its policy.
/// Stops early when a position is missing from the choice map (partial searches).
fn build_trace(root: &State, root_move: Move, choices: &ChoiceMap) -> Vec<TraceStep> {
    let mut steps = Vec::new();
    let mut s = root.clone();
    let mut next_move = Some(root_move);
    let mut finished = false;

    while !finished {
        let mv = match next_move.take() {
            Some(mv) => mv,
            None => match choices.get(&memo_key(b'P', &s)) {
                Some(&packed) => unpack_move(packed),
                None => break,
            },
        };
        let (child, card) = match apply_player_move(&s, mv) {
            Some(applied) => applied,
            None => break,
        };
        s = child;
        steps.push(TraceStep::after(TraceAction::Player { mv, card }, &s));

        if mv.kind == MoveKind::SleeveTop {
            continue; // sleeving does not end the turn
        }

        // after_player_action: unpass check, early resolution when both stand pat.
        opponent_unpass_check(&mut s);
        if s.p.passed && s.o.passed {
            opponent_unpass_check(&mut s);
            if s.o.passed {
                steps.push(resolve_step(&s));
                break;
            }
        }

        // run loop: opponent turns until the player has to choose again.
        loop {
            if s.p.passed && s.o.passed {
                steps.push(resolve_step(&s));
                finished = true;
                break;
            }
            if !s.o.passed {
                match opponent_act(&mut s) {
                    OpponentAction::Draw(card) => {
                        steps.push(TraceStep::after(TraceAction::OpponentDraw { card }, &s))
                    }
                    OpponentAction::Pass => {
                        steps.push(TraceStep::after(TraceAction::OpponentPass, &s))
                    }
                }
                opponent_unpass_check(&mut s);
            }
            if !s.p.passed {
                break; // player's turn again
            }
            opponent_unpass_check(&mut s);
            if s.p.passed && s.o.passed {
                opponent_unpass_check(&mut s);
                if s.o.passed {
                    steps.push(resolve_step(&s));
                    finished = true;
                    break;
                }
            }
        }
    }
    steps
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

/// Progress nudge: among the moves that tie the best (non-positive) value, prefer
/// playing over passing so the position keeps moving. A dead top card cycles the deck;
/// any top or sleeve play that advances the deck is next, so a lost round never freezes
/// on a pass while cards remain. A dead sleeve card clears the sleeve, and when the draw
/// pile is already empty any sleeve play that removes the card qualifies. A card that
/// puts itself back into the sleeve is never chosen by this rule, since it would be
/// played every turn.
fn progress_move(root: &State, evaluations: &[Eval], best: f32) -> Move {
    const EPS: f32 = 0.0001;
    let deck_empty = root.p.top().is_none();
    let mut progress = None;
    let mut dead: SmallVec<[Move; 8]> = SmallVec::new();
    let mut other: SmallVec<[Move; 8]> = SmallVec::new();

    for eval in evaluations {
        if eval.value < best - EPS {
            continue;
        }
        match eval.mv.kind {
            MoveKind::PlayTop => {
                if let Some(top) = root.p.top() {
                    if top.is_dead() {
                        return eval.mv;
                    }
                    if progress.is_none() {
                        progress = Some(eval.mv);
                    }
                }
            }
            MoveKind::SleeveTop => {
                if progress.is_none() {
                    progress = Some(eval.mv);
                }
            }
            MoveKind::PlaySleeve => {
                let index = eval.mv.sleeve_index;
                if index >= 0
                    && let Some(card) = root.p.sleeve.get(index as usize)
                {
                    if card.is_dead() {
                        dead.push(eval.mv);
                    } else if deck_empty {
                        other.push(eval.mv);
                    }
                }
            }
            _ => {}
        }
    }

    if let Some(mv) = progress {
        return mv;
    }
    for mv in dead.into_iter().chain(other) {
        if sleeve_play_removes_card(root, mv) {
            return mv;
        }
    }
    Move::pass()
}

/// True when playing `mv` lowers how many cards equal to it sit in the sleeve. Counting
/// occurrences (instead of asking whether an equal card remains) keeps duplicate-valued
/// cards distinct: only a card that puts itself back keeps the count from dropping.
fn sleeve_play_removes_card(root: &State, mv: Move) -> bool {
    let Some(card) = root.p.sleeve.get(mv.sleeve_index as usize) else {
        return false;
    };
    let before = root.p.sleeve.iter().filter(|c| *c == card).count();
    match apply_player_move(root, mv) {
        Some((child, _)) => child.p.sleeve.iter().filter(|c| *c == card).count() < before,
        None => false,
    }
}
