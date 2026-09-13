//! The flat binary protocol shared with the managed plugin.
//!
//! The managed writer lives in `Native/SolverProtocol.cs`; both sides must be changed
//! together when the layout changes, and [`VERSION`] is bumped so a mismatched pair
//! fails loudly instead of misreading memory. All integers are little-endian.
//!
//! Input (state):
//! ```text
//! u32 version; u32 flags (1 uprising, 2 supper)
//! i32 plain_target, p_mods, o_mods, holds_at, insight_left, sleeve_size, payable
//! u32 cost_count; i32[cost_count] sleeve_costs
//! side P, side O
//! ```
//! Side: `i32 deck_pos, capacity, bet; u32 sleeve_draws, passed, out_of_cards,
//! discard_count;` then the deck, sleeve and table zones. A zone is `u32 len` followed
//! by cards. A card is `u32 value_count; i32[value_count] values; u32 flags`.
//!
//! Batch input appends `u32 candidate_count` and per candidate a length-framed op block:
//! `u32 byte_len` followed by ops: `u32 kind; u32 side; i32 a; i32 b; u32 list_len;
//! u32[list_len]`. Kind 1 = SetDeckOrder (list = card indices of the base deck),
//! kind 2 = TakeDeckToSleeve (`a` = deck index), kind 3 = MoveTableToSleeve
//! (`a` = table index).

use crate::model::{Card, MAX_VALUES, Side, State};
use crate::solver::{MoveKind, Note, Outcome, SleeveReason};

pub const VERSION: u32 = 1;

pub const ERR_VERSION: i32 = -1;
pub const ERR_TRUNCATED: i32 = -2;
pub const ERR_CARD: i32 = -3;
pub const ERR_OP: i32 = -4;

pub const OP_SET_DECK_ORDER: u32 = 1;
pub const OP_TAKE_DECK_TO_SLEEVE: u32 = 2;
pub const OP_MOVE_TABLE_TO_SLEEVE: u32 = 3;

const MAX_ZONE: usize = 512;
const MAX_CANDIDATE_BYTES: usize = 1 << 16;
const MAX_OPS_PER_CANDIDATE: usize = 16;

pub struct Reader<'a> {
    data: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    pub fn new(data: &'a [u8]) -> Self {
        Reader { data, pos: 0 }
    }

    pub fn remaining(&self) -> usize {
        self.data.len().saturating_sub(self.pos)
    }

    pub fn bytes(&mut self, count: usize) -> Result<&'a [u8], i32> {
        let end = self.pos.checked_add(count).ok_or(ERR_TRUNCATED)?;
        if end > self.data.len() {
            return Err(ERR_TRUNCATED);
        }
        let slice = &self.data[self.pos..end];
        self.pos = end;
        Ok(slice)
    }

    /// Read the next `count` bytes as a standalone reader (used for length-framed blocks).
    pub fn sub(&mut self, count: usize) -> Result<Reader<'a>, i32> {
        let bytes = self.bytes(count)?;
        Ok(Reader {
            data: bytes,
            pos: 0,
        })
    }

    pub fn u8(&mut self) -> Result<u8, i32> {
        Ok(self.bytes(1)?[0])
    }

    pub fn u32(&mut self) -> Result<u32, i32> {
        let bytes = self.bytes(4)?;
        Ok(u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]))
    }

    pub fn i32(&mut self) -> Result<i32, i32> {
        Ok(self.u32()? as i32)
    }
}

pub fn parse_state(r: &mut Reader) -> Result<State, i32> {
    let version = r.u32()?;
    if version != VERSION {
        return Err(ERR_VERSION);
    }
    let flags = r.u32()?;
    let plain_target = r.i32()?;
    let p_mods = r.i32()?;
    let o_mods = r.i32()?;
    let holds_at = r.i32()?;
    let insight_left = r.i32()?;
    let sleeve_size = r.i32()?;
    let payable = r.i32()?;

    let cost_count = r.u32()? as usize;
    if cost_count > MAX_ZONE {
        return Err(ERR_TRUNCATED);
    }
    let mut sleeve_costs = Vec::with_capacity(cost_count);
    for _ in 0..cost_count {
        sleeve_costs.push(r.i32()?);
    }

    let p = parse_side(r)?;
    let o = parse_side(r)?;

    let mut state = State::new();
    state.plain_target = plain_target;
    state.p_mods = p_mods;
    state.o_mods = o_mods;
    state.holds_at = holds_at;
    state.insight_left = insight_left;
    state.sleeve_size = sleeve_size;
    state.payable = payable;
    state.sleeve_costs = sleeve_costs;
    state.uprising = flags & 1 != 0;
    state.supper = flags & 2 != 0;
    state.p = p;
    state.o = o;
    Ok(state)
}

fn parse_side(r: &mut Reader) -> Result<Side, i32> {
    let mut side = Side::new();
    side.deck_pos = r.i32()?.max(0) as u32;
    side.capacity = r.i32()?;
    side.bet = r.i32()?;
    side.sleeve_draws = r.u32()?;
    side.passed = r.u32()? != 0;
    side.out_of_cards = r.u32()? != 0;
    side.discard_count = r.u32()?;
    side.deck = parse_zone(r)?.into();
    side.sleeve = parse_zone(r)?.into_iter().collect();
    side.table = parse_zone(r)?.into_iter().collect();
    Ok(side)
}

fn parse_zone(r: &mut Reader) -> Result<Vec<Card>, i32> {
    let len = r.u32()? as usize;
    if len > MAX_ZONE {
        return Err(ERR_TRUNCATED);
    }
    let mut cards = Vec::with_capacity(len);
    for _ in 0..len {
        cards.push(parse_card(r)?);
    }
    Ok(cards)
}

fn parse_card(r: &mut Reader) -> Result<Card, i32> {
    let count = r.u32()? as usize;
    if count == 0 || count > MAX_VALUES {
        return Err(ERR_CARD);
    }
    let mut values = [0i32; MAX_VALUES];
    for value in values.iter_mut().take(count) {
        *value = r.i32()?;
    }
    let flags = r.u32()?;
    if flags > u8::MAX as u32 {
        return Err(ERR_CARD);
    }
    Ok(Card {
        values,
        value_count: count as u8,
        flags: flags as u8,
    })
}

/// One state mutation in a batch candidate. Constructed by the managed plugin, applied
/// here so the mutation semantics (sleeve trimming, capacity, discard counts) live in
/// exactly one place.
pub struct Op {
    pub kind: u32,
    pub side: usize,
    pub a: i32,
    pub b: i32,
    pub list: Vec<u32>,
}

pub fn parse_candidate(r: &mut Reader) -> Result<Vec<Op>, i32> {
    // Length-framed: `u32 byte_len` followed by that many bytes of ops. Framing by bytes
    // instead of op count keeps the parser robust if op records ever grow.
    let len = r.u32()? as usize;
    if len > MAX_CANDIDATE_BYTES {
        return Err(ERR_OP);
    }
    let mut ops = Vec::new();
    let mut sub = r.sub(len)?;
    while sub.remaining() > 0 {
        if ops.len() >= MAX_OPS_PER_CANDIDATE {
            return Err(ERR_OP);
        }
        ops.push(parse_op(&mut sub)?);
    }
    Ok(ops)
}

fn parse_op(r: &mut Reader) -> Result<Op, i32> {
    let kind = r.u32()?;
    let side = r.u32()? as usize;
    if side > 1 {
        return Err(ERR_OP);
    }
    let a = r.i32()?;
    let b = r.i32()?;
    let list_len = r.u32()? as usize;
    if list_len > MAX_ZONE {
        return Err(ERR_OP);
    }
    let mut list = Vec::with_capacity(list_len);
    for _ in 0..list_len {
        list.push(r.u32()?);
    }
    Ok(Op {
        kind,
        side,
        a,
        b,
        list,
    })
}

pub fn apply_ops(state: &mut State, ops: &[Op]) -> Result<(), i32> {
    for op in ops {
        apply_op(state, op)?;
    }
    Ok(())
}

fn apply_op(state: &mut State, op: &Op) -> Result<(), i32> {
    match op.kind {
        OP_SET_DECK_ORDER => {
            let side = side_mut(state, op.side);
            if op.list.len() != side.deck.len() {
                return Err(ERR_OP);
            }
            let mut next = Vec::with_capacity(op.list.len());
            for &index in &op.list {
                next.push(*side.deck.get(index as usize).ok_or(ERR_OP)?);
            }
            side.deck = next.into();
            Ok(())
        }
        OP_TAKE_DECK_TO_SLEEVE => {
            let index = op.a;
            let card = {
                let side = side_mut(state, op.side);
                if index < 0 || index as usize >= side.deck.len() {
                    return Err(ERR_OP);
                }
                // This runs once per candidate, outside the search; a copy is cheap here
                // and keeps the shared pile of the captured base intact.
                let mut cards = side.deck.to_vec();
                let card = cards.remove(index as usize);
                side.deck = cards.into();
                card
            };
            sleeve_free(state, op.side, card);
            Ok(())
        }
        OP_MOVE_TABLE_TO_SLEEVE => {
            let index = op.a;
            let card = {
                let side = side_mut(state, op.side);
                if index < 0 || index as usize >= side.table.len() {
                    return Err(ERR_OP);
                }
                let card = side.table.remove(index as usize);
                if !card.is_hollow() {
                    // The vacated slot is usable again (a hollow card takes its own slot with it).
                    side.capacity += 1;
                }
                card
            };
            sleeve_free(state, op.side, card);
            state.invalidate_values();
            Ok(())
        }
        _ => Err(ERR_OP),
    }
}

/// Insert a card into a sleeve for free: trim the oldest when full, or discard it
/// straight away when the sleeve is disabled. Mirrors `SleeveUI.PlaceCard`.
fn sleeve_free(state: &mut State, side_index: usize, card: Card) {
    let sleeve_size = state.sleeve_size;
    let side = side_mut(state, side_index);
    if sleeve_size <= 0 {
        side.discard_count += 1;
        return;
    }
    let max = sleeve_size.max(1) as usize;
    while side.sleeve.len() >= max {
        side.sleeve.remove(0);
    }
    side.sleeve.push(card);
}

fn side_mut(state: &mut State, side: usize) -> &mut Side {
    if side == 0 {
        &mut state.p
    } else {
        &mut state.o
    }
}

/// Little-endian output writer. The managed side reads the same field order.
pub struct Writer {
    buf: Vec<u8>,
}

impl Writer {
    pub fn new() -> Self {
        Writer {
            buf: Vec::with_capacity(256),
        }
    }

    pub fn u32(&mut self, value: u32) {
        self.buf.extend_from_slice(&value.to_le_bytes());
    }

    pub fn i32(&mut self, value: i32) {
        self.buf.extend_from_slice(&value.to_le_bytes());
    }

    pub fn u64(&mut self, value: u64) {
        self.buf.extend_from_slice(&value.to_le_bytes());
    }

    pub fn f64(&mut self, value: f64) {
        self.buf.extend_from_slice(&value.to_le_bytes());
    }

    pub fn into_vec(self) -> Vec<u8> {
        self.buf
    }
}

impl Default for Writer {
    fn default() -> Self {
        Self::new()
    }
}

pub fn write_solve_result(out: &mut Writer, outcome: &Outcome) {
    out.u32(VERSION);
    out.i32(0);
    out.i32(move_kind_code(outcome.best.kind));
    out.i32(outcome.best.sleeve_index);
    out.u32(outcome.best.to_opponent as u32);
    out.f64(outcome.value as f64);
    out.u64(outcome.nodes);
    out.u32(outcome.aborted as u32);
    out.u32(note_code(outcome.note));
    out.i32(outcome.p_value);
    out.i32(outcome.o_value);
    out.i32(outcome.p_target);
    out.i32(outcome.o_target);
    out.i32(outcome.payable);
    out.i32(outcome.sleeve_cost);

    let mut flags = 0u32;
    if outcome.can_pass {
        flags |= 1;
    }
    if outcome.can_sleeve {
        flags |= 2;
    }
    if outcome.top_null {
        flags |= 4;
    }
    if outcome.own_table_full {
        flags |= 8;
    }
    if outcome.opponent_table_full {
        flags |= 16;
    }
    out.u32(flags);
    out.u32(sleeve_reason_code(outcome.sleeve_reason));

    out.u32(outcome.legal.len() as u32);
    out.u32(outcome.evaluations.len() as u32);
    for mv in &outcome.legal {
        out.i32(move_kind_code(mv.kind));
        out.i32(mv.sleeve_index);
        out.u32(mv.to_opponent as u32);
    }
    for eval in &outcome.evaluations {
        out.f64(eval.value as f64);
    }
}

pub fn write_evaluate_result(out: &mut Writer, value: f32) {
    out.u32(VERSION);
    out.i32(0);
    out.f64(value as f64);
}

pub fn write_batch_result(out: &mut Writer, values: &[f32]) {
    out.u32(VERSION);
    out.i32(0);
    out.u32(values.len() as u32);
    for value in values {
        out.f64(*value as f64);
    }
}

fn move_kind_code(kind: MoveKind) -> i32 {
    match kind {
        MoveKind::Pass => 0,
        MoveKind::PlayTop => 1,
        MoveKind::SleeveTop => 2,
        MoveKind::PlaySleeve => 3,
    }
}

fn note_code(note: Note) -> u32 {
    match note {
        Note::None => 0,
        Note::ProgressTieBreak => 1,
    }
}

fn sleeve_reason_code(reason: SleeveReason) -> u32 {
    match reason {
        SleeveReason::Available => 0,
        SleeveReason::SizeZero => 1,
        SleeveReason::DrawPileEmpty => 2,
        SleeveReason::CannotPay => 3,
        SleeveReason::Unavailable => 4,
    }
}
