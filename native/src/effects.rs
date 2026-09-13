//! Card effect semantics, reduced to the deterministic core of the game's
//! `CardEffect` subclasses. The managed mapper in `Effects.cs` is the only producer
//! of the descriptors interpreted here; every op mirrors one decompiled behavior.
//!
//! Cards are addressed by [`Spot`]: a zone plus an index. Table positions use
//! placement order as the slot approximation (the game randomizes the opponent's
//! slot choice, so placement order is the only deterministic model).

use smallvec::SmallVec;

use crate::model::{Card, Effect, Side, State, TYPE_BROKEN, TYPE_DRAIN, TYPE_TARGET};

// Ops (`Effect.op`).
pub const OP_NONE: u8 = 0;
pub const OP_BREAK: u8 = 1;
pub const OP_MEND: u8 = 2;
pub const OP_ADD_VALUE: u8 = 3;
pub const OP_SET_VALUE: u8 = 4;
pub const OP_INVERT: u8 = 5;
pub const OP_DRAIN: u8 = 6;
pub const OP_HOLLOW: u8 = 7;
pub const OP_DISCARD: u8 = 8;
pub const OP_EXHAUST: u8 = 9;
pub const OP_MOVE: u8 = 10;
pub const OP_DRAW: u8 = 11;
pub const OP_DUPLICATE: u8 = 12;
pub const OP_EXPLOIT: u8 = 13;
pub const OP_COINS: u8 = 14;
pub const OP_SKIP_TURN: u8 = 15;
pub const OP_TRIGGER: u8 = 16;
pub const OP_SWAP: u8 = 17;
pub const OP_INSIGHT: u8 = 18;
pub const OP_IGNITE: u8 = 19;

// Target kinds (`Effect.target`).
pub const TARGET_NONE: u8 = 0;
pub const TARGET_RELATIVE: u8 = 1;
pub const TARGET_GATHER: u8 = 2;
pub const TARGET_RELATIVE_AND_GATHER: u8 = 3;
pub const TARGET_ALL_TABLES: u8 = 4;
pub const TARGET_SOURCE: u8 = 5;

// `CoinZoneHelper.EOwner` bits.
pub const OWNER_SELF: i32 = 1;
pub const OWNER_OTHER: i32 = 2;
pub const OWNER_PLAYER: i32 = 4;
pub const OWNER_OPPONENT: i32 = 8;

// `ELocation` bits.
pub const LOC_DRAW: i32 = 1;
pub const LOC_DISCARD: i32 = 2;
pub const LOC_TABLE: i32 = 4;
pub const LOC_SLEEVE: i32 = 8;

// Coin zones.
pub const ZONE_STASH: i32 = 1;
pub const ZONE_BET: i32 = 2;
pub const ZONE_LIMBO: i32 = 4;

// `CardPosition.ERelativeCardPosition` bits.
pub const REL_LEFT: i32 = 1;
pub const REL_RIGHT: i32 = 2;
pub const REL_OPPOSITE: i32 = 4;
pub const REL_ADJACENT: i32 = 8;
pub const REL_SELF: i32 = 16;
pub const REL_ON_TOP_FIRST: i32 = 32;
pub const REL_ON_TOP_ALL: i32 = 64;
pub const REL_OPPOSITE_PLUS_ADJACENTS: i32 = 128;
pub const REL_LEFT_RANGE: i32 = 256;
pub const REL_RIGHT_RANGE: i32 = 512;
pub const REL_SAME_SLOT: i32 = 1024;
pub const REL_OPPOSITE_RIGHT: i32 = 2048;
pub const REL_OPPOSITE_LEFT: i32 = 4096;

// Value scopes (`CardValueTempModifier.ETargetValues`).
pub const SCOPE_ALL: i32 = 0;
pub const SCOPE_HIGHEST: i32 = 1;
pub const SCOPE_LOWEST: i32 = 2;
pub const SCOPE_FIRST: i32 = 3;

// Adjustment types (`CardValueTempModifier.EAdjustmentType`).
pub const ADJ_RELATIVE: u8 = 0;
pub const ADJ_ABSOLUTE: u8 = 1;
pub const ADJ_INVERT: u8 = 2;
pub const ADJ_BREAK: u8 = 3;
pub const ADJ_MEND: u8 = 4;

/// Where a card currently sits. `index` is the table placement order, the sleeve
/// order, or the position in the draw pile counted from the top (`0` = top).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Spot {
    pub side: usize,
    pub zone: u8,
    pub index: usize,
}

pub const ZONE_TABLE: u8 = 0;
pub const ZONE_SLEEVE: u8 = 1;
pub const ZONE_DRAW: u8 = 2;
pub const ZONE_DISCARD: u8 = 3;

impl Spot {
    pub fn table(side: usize, index: usize) -> Self {
        Spot {
            side,
            zone: ZONE_TABLE,
            index,
        }
    }

    pub fn sleeve(side: usize, index: usize) -> Self {
        Spot {
            side,
            zone: ZONE_SLEEVE,
            index,
        }
    }

    pub fn draw(side: usize, from_top: usize) -> Self {
        Spot {
            side,
            zone: ZONE_DRAW,
            index: from_top,
        }
    }
}

fn side_ref(state: &State, side: usize) -> &Side {
    if side == 0 { &state.p } else { &state.o }
}

fn side_mut(state: &mut State, side: usize) -> &mut Side {
    if side == 0 {
        &mut state.p
    } else {
        &mut state.o
    }
}

/// The card at `spot`, if the spot is still occupied.
pub fn card_at(state: &State, spot: Spot) -> Option<Card> {
    let side = side_ref(state, spot.side);
    match spot.zone {
        ZONE_TABLE => side.table.get(spot.index).copied(),
        ZONE_SLEEVE => side.sleeve.get(spot.index).copied(),
        ZONE_DRAW => {
            let index = side.deck_pos as usize + spot.index;
            side.deck.get(index).copied()
        }
        ZONE_DISCARD => None,
        _ => None,
    }
}

/// Remove the card at `spot` from its zone; the discard pile never needs contents.
fn take_card(state: &mut State, spot: Spot) -> Option<Card> {
    let hollow_refund = |card: &Card| card.is_hollow();
    match spot.zone {
        ZONE_TABLE => {
            let side = side_mut(state, spot.side);
            if spot.index >= side.table.len() {
                return None;
            }
            let card = side.table.remove(spot.index);
            if !hollow_refund(&card) {
                side.capacity += 1;
            }
            state.invalidate_values();
            Some(card)
        }
        ZONE_SLEEVE => {
            let side = side_mut(state, spot.side);
            if spot.index >= side.sleeve.len() {
                return None;
            }
            Some(side.sleeve.remove(spot.index))
        }
        ZONE_DRAW => {
            let side = side_mut(state, spot.side);
            let index = side.deck_pos as usize + spot.index;
            if index >= side.deck.len() {
                return None;
            }
            // Draw piles are shared through the search, so removing a card copies
            // the pile the same way the batch deck-order op does.
            let mut cards = side.deck.to_vec();
            let card = cards.remove(index);
            side.deck = cards.into();
            Some(card)
        }
        _ => None,
    }
}

/// Push a card onto `side`'s table, honouring the hollow capacity refund.
fn push_table(state: &mut State, side_index: usize, card: Card, check_space: bool) -> bool {
    let side = side_mut(state, side_index);
    if check_space && side.capacity <= 0 {
        return false;
    }
    side.table.push(card);
    side.capacity -= 1;
    if card.is_hollow() {
        side.capacity += 1;
    }
    state.invalidate_values();
    true
}

/// Put a card into `side`'s sleeve, mirroring `SleeveUI.PlaceCard` (trim the oldest,
/// discard everything when the sleeve is disabled).
fn push_sleeve(state: &mut State, side_index: usize, card: Card) {
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

/// Insert a card into `side`'s draw pile at `from_top` (0 = top).
fn push_draw(state: &mut State, side_index: usize, from_top_offset: usize, card: Card) {
    let side = side_mut(state, side_index);
    let mut cards = side.deck.to_vec();
    let base = side.deck_pos as usize;
    let index = (base + from_top_offset).min(cards.len());
    cards.insert(index, card);
    side.deck = cards.into();
}

fn discard(state: &mut State, spot: Spot) -> Option<Card> {
    let card = take_card(state, spot)?;
    side_mut(state, spot.side).discard_count += 1;
    Some(card)
}

fn push_unique(out: &mut SmallVec<[Spot; 8]>, spot: Spot) {
    if out.iter().all(|existing| {
        existing.side != spot.side || existing.zone != spot.zone || existing.index != spot.index
    }) {
        out.push(spot);
    }
}

/// `CardPosition.GetCards` for a table source, in the engine's enumeration order.
fn relative_targets(state: &State, source: Spot, flags: i32) -> SmallVec<[Spot; 8]> {
    let mut out: SmallVec<[Spot; 8]> = SmallVec::new();
    if source.zone != ZONE_TABLE {
        return out;
    }
    let own = source.side;
    let other = 1 - own;
    let index = source.index;
    let own_len = side_ref(state, own).table.len();
    let other_len = side_ref(state, other).table.len();

    let mut push = |side: usize, at: usize| {
        let len = if side == own { own_len } else { other_len };
        if at < len {
            push_unique(&mut out, Spot::table(side, at));
        }
    };

    if flags & REL_ADJACENT != 0 {
        if index > 0 {
            push(own, index - 1);
        }
        push(own, index + 1);
    } else {
        if flags & REL_LEFT != 0 && index > 0 {
            push(own, index - 1);
        }
        if flags & REL_RIGHT != 0 {
            push(own, index + 1);
        }
    }
    if flags & REL_LEFT_RANGE != 0 {
        for at in (0..index).rev() {
            push(own, at);
        }
    }
    if flags & REL_RIGHT_RANGE != 0 {
        for at in index + 1..own_len {
            push(own, at);
        }
    }
    if flags & REL_OPPOSITE != 0 {
        push(other, index);
    }
    if flags & REL_OPPOSITE_PLUS_ADJACENTS != 0 {
        push(other, index);
        if index > 0 {
            push(other, index - 1);
        }
        push(other, index + 1);
    }
    if flags & (REL_SELF | REL_ON_TOP_FIRST | REL_ON_TOP_ALL | REL_SAME_SLOT) != 0 {
        push(own, index);
    }
    if flags & REL_OPPOSITE_LEFT != 0 && index > 0 {
        push(other, index - 1);
    }
    if flags & REL_OPPOSITE_RIGHT != 0 {
        push(other, index + 1);
    }
    out
}

/// Resolve `_owner` bits against the executor, in the engine's fixed check order.
fn owner_sides(executor: usize, owner: i32) -> SmallVec<[usize; 2]> {
    let mut sides = SmallVec::new();
    if owner & OWNER_PLAYER != 0 {
        sides.push(0);
    }
    if owner & OWNER_OPPONENT != 0 {
        sides.push(1);
    }
    if owner & OWNER_SELF != 0 && sides.iter().all(|&side| side != executor) {
        sides.push(executor);
    }
    if owner & OWNER_OTHER != 0 && sides.iter().all(|&side| side != 1 - executor) {
        sides.push(1 - executor);
    }
    sides
}

fn gather_location(state: &State, side: usize, location: i32) -> SmallVec<[Spot; 8]> {
    let mut out: SmallVec<[Spot; 8]> = SmallVec::new();
    if location & LOC_TABLE != 0 {
        for index in 0..side_ref(state, side).table.len() {
            push_unique(&mut out, Spot::table(side, index));
        }
    }
    if location & LOC_DRAW != 0 {
        let side_ref_value = side_ref(state, side);
        for index in 0..side_ref_value
            .deck
            .len()
            .saturating_sub(side_ref_value.deck_pos as usize)
        {
            push_unique(&mut out, Spot::draw(side, index));
        }
    }
    if location & LOC_DISCARD != 0 {
        // Discard contents are not modeled; only the count is known.
        for index in 0..side_ref(state, side).discard_count as usize {
            push_unique(
                &mut out,
                Spot {
                    side,
                    zone: ZONE_DISCARD,
                    index,
                },
            );
        }
    }
    if location & LOC_SLEEVE != 0 && side == 0 {
        for index in 0..side_ref(state, side).sleeve.len() {
            push_unique(&mut out, Spot::sleeve(side, index));
        }
    }
    out
}

fn apply_take_filter(state: &State, targets: &mut SmallVec<[Spot; 8]>, filter: u32) {
    let kind = filter & 0xF;
    match kind {
        // TakeNum: keep the first/last `count` targets.
        1 => {
            let mode = (filter >> 4) & 0x3;
            let count = ((filter >> 6) & 0xFFF) as usize;
            match mode {
                0 => targets.truncate(count),
                1 => {
                    if targets.len() > count {
                        let drop = targets.len() - count;
                        targets.drain(..drop);
                    }
                }
                // Random selection cannot be modeled deterministically; the mapper marks
                // those effects unmodeled and the first cards stand in here.
                _ => targets.truncate(count),
            }
        }
        // IsBrokenCardFilter: keep only cards with a Broken value. The game's filter
        // ignores its `_isBroken` field and always matches broken cards.
        2 => targets.retain(|spot| card_at(state, *spot).is_some_and(|card| card.is_broken())),
        _ => {}
    }
}

/// Cards selected by an effect's target specification.
pub fn gather_targets(state: &State, source: Spot, effect: &Effect) -> SmallVec<[Spot; 8]> {
    let mut out: SmallVec<[Spot; 8]> = SmallVec::new();
    let exclude_source = effect.flags & crate::model::EF_EXCLUDE_SOURCE != 0;
    match effect.target {
        TARGET_SOURCE => push_unique(&mut out, source),
        TARGET_RELATIVE => {
            out = relative_targets(state, source, effect.t1);
        }
        TARGET_GATHER => {
            for side in owner_sides(source.side, effect.t1) {
                for spot in gather_location(state, side, effect.t2) {
                    if exclude_source && spot == source {
                        continue;
                    }
                    push_unique(&mut out, spot);
                }
            }
        }
        TARGET_RELATIVE_AND_GATHER => {
            // The exclusion and the card filter apply to the gather part only: the game
            // concatenates the relative targets after filtering the gathered ones.
            out = relative_targets(state, source, effect.t1);
            for side in owner_sides(source.side, effect.t2) {
                for spot in gather_location(state, side, effect.t3) {
                    if exclude_source && spot == source {
                        continue;
                    }
                    push_unique(&mut out, spot);
                }
            }
        }
        TARGET_ALL_TABLES => {
            for side in 0..2 {
                for index in 0..side_ref(state, side).table.len() {
                    push_unique(&mut out, Spot::table(side, index));
                }
            }
        }
        _ => {}
    }
    apply_take_filter(state, &mut out, effect.filter);
    out
}

// Condition kinds (`ActivationCondition` subclasses the search can evaluate).
pub const COND_NONE: u8 = 0;
pub const COND_HAS_BLACKJACK: u8 = 1;
pub const COND_COINS: u8 = 2;
pub const COND_CAN_RAISE: u8 = 3;
pub const COND_BLIND: u8 = 4;

fn compare(mode: u8, left: i32, right: i32) -> bool {
    // Mirrors `ValueComparison.Compare`; every condition enum value maps in range.
    match mode {
        0 => left == right,
        1 => left != right,
        2 => left < right,
        3 => left <= right,
        4 => left > right,
        5 => left >= right,
        _ => false,
    }
}

/// The container's activation conditions, evaluated exactly for the supported set.
pub fn condition_ok(state: &State, effect: &Effect, executor: usize) -> bool {
    match effect.cond {
        COND_HAS_BLACKJACK => {
            let has = if executor == 0 {
                state.p_bj()
            } else {
                state.o_bj()
            };
            has == (effect.cond_a != 0)
        }
        COND_COINS => {
            // The managed mapper only emits this when both coin operands are constants.
            compare(effect.cond_cmp, effect.cond_a, effect.cond_b)
        }
        COND_CAN_RAISE => side_ref(state, executor).stash >= effect.cond_a,
        COND_BLIND => compare(effect.cond_cmp, effect.cond_a, state.blind),
        _ => true,
    }
}

fn for_each_table_card(state: &mut State, spots: &[Spot], mut f: impl FnMut(&mut Card)) {
    for &spot in spots {
        if spot.zone != ZONE_TABLE {
            continue;
        }
        let side = side_mut(state, spot.side);
        if let Some(card) = side.table.get_mut(spot.index) {
            f(card);
        }
    }
    state.invalidate_values();
}

/// `ApplyBroken` + `BrokenModifier`: break every eligible scoped value and mark the
/// card's typed values broken.
fn break_card(card: &mut Card, scope: i32) {
    let eligible = card
        .scope_indices(scope)
        .iter()
        .any(|&index| card.has_type(index, TYPE_TARGET) && !card.has_type(index, TYPE_BROKEN));
    if !eligible {
        return;
    }
    for index in card.scope_indices(scope) {
        if card.has_type(index, TYPE_TARGET | TYPE_DRAIN) && !card.has_type(index, TYPE_BROKEN) {
            card.values[index] = -card.values[index].abs();
        }
    }
    for types in card.types.iter_mut().take(card.value_count as usize) {
        if *types != 0 {
            *types |= TYPE_BROKEN;
        }
    }
}

/// `ApplyMend` + `MendModifier`: restore broken scoped values and clear the type.
fn mend_card(card: &mut Card, scope: i32) {
    let eligible = card
        .scope_indices(scope)
        .iter()
        .any(|&index| card.has_type(index, TYPE_TARGET) && card.has_type(index, TYPE_BROKEN));
    if !eligible {
        return;
    }
    for index in card.scope_indices(scope) {
        if card.has_type(index, TYPE_TARGET) && card.has_type(index, TYPE_BROKEN) {
            card.values[index] = card.values[index].abs();
        }
    }
    for types in card.types.iter_mut().take(card.value_count as usize) {
        *types &= !TYPE_BROKEN;
    }
}

/// Resolve one `CoinZoneHelper.EOwner`/`CardLocationConfig.EOwner` selector against the
/// executor, in the engine's fixed check order (Player, Opponent, Self, Other).
fn owner_side(executor: usize, owner: i32) -> Option<usize> {
    if owner & OWNER_PLAYER != 0 {
        return Some(0);
    }
    if owner & OWNER_OPPONENT != 0 {
        return Some(1);
    }
    if owner & OWNER_SELF != 0 {
        return Some(executor);
    }
    if owner & OWNER_OTHER != 0 {
        return Some(1 - executor);
    }
    None
}

fn decode_zone(executor: usize, packed: i32) -> Option<(usize, i32)> {
    let owner = packed & 0xFF;
    let zone = (packed >> 8) & 0xFF;
    if zone == 0 {
        return None;
    }
    owner_side(executor, owner).map(|side| (side, zone))
}

fn zone_value(state: &State, side: usize, zone: i32) -> i32 {
    match zone {
        ZONE_STASH => side_ref(state, side).stash,
        ZONE_BET => side_ref(state, side).bet,
        ZONE_LIMBO => state.pot,
        _ => 0,
    }
}

fn add_zone(state: &mut State, side: usize, zone: i32, amount: i32) {
    match zone {
        ZONE_STASH => side_mut(state, side).stash += amount,
        ZONE_BET => side_mut(state, side).bet += amount,
        ZONE_LIMBO => state.pot += amount,
        _ => {}
    }
}

/// Move `amount` (coin value) between zones. `src == 0` creates coins, `dst == 0`
/// destroys them, mirroring `CoinsEffect`.
pub fn move_coins(state: &mut State, executor: usize, src: i32, dst: i32, amount: i32) {
    if amount <= 0 {
        return;
    }
    let moved = if src == 0 {
        amount
    } else {
        let Some((side, zone)) = decode_zone(executor, src) else {
            return;
        };
        let available = zone_value(state, side, zone).max(0);
        let moved = available.min(amount);
        add_zone(state, side, zone, -moved);
        moved
    };
    if dst != 0 && moved > 0 {
        let Some((side, zone)) = decode_zone(executor, dst) else {
            return;
        };
        add_zone(state, side, zone, moved);
    }
    state.sync_payable();
}

/// Apply one effect of a card that sits at `source`. Conditions are checked by the
/// caller through [`condition_ok`]; `depth` bounds `TriggerCardEffects` recursion.
pub fn apply_effect(state: &mut State, source: Spot, effect: &Effect, depth: u8) {
    match effect.op {
        OP_BREAK => {
            let spots = gather_targets(state, source, effect);
            for_each_table_card(state, &spots, |card| break_card(card, effect.a));
        }
        OP_MEND => {
            let spots = gather_targets(state, source, effect);
            for_each_table_card(state, &spots, |card| mend_card(card, effect.a));
        }
        OP_ADD_VALUE => {
            let spots = gather_targets(state, source, effect);
            for_each_table_card(state, &spots, |card| {
                card.adjust_values(effect.a, effect.b, ADJ_RELATIVE)
            });
        }
        OP_SET_VALUE => {
            let spots = gather_targets(state, source, effect);
            for_each_table_card(state, &spots, |card| {
                card.adjust_values(effect.a, effect.b, ADJ_ABSOLUTE)
            });
        }
        OP_INVERT => {
            // `InvertValue` targets N cards but adds the modifier to the source card
            // N times; the type clamp turns every value into zero.
            let spots = gather_targets(state, source, effect);
            if !spots.is_empty() && source.zone == ZONE_TABLE {
                for_each_table_card(state, &[source], |card| {
                    card.adjust_values(SCOPE_ALL, 0, ADJ_INVERT)
                });
            }
        }
        OP_DRAIN => {
            let spots = gather_targets(state, source, effect);
            let mut drained = 0i32;
            for &spot in &spots {
                if spot.zone != ZONE_TABLE {
                    continue;
                }
                let side = side_mut(state, spot.side);
                if let Some(card) = side.table.get_mut(spot.index)
                    && card.value_count > 0
                    && card.has_type(0, TYPE_TARGET)
                    && card.values[0] != 0
                {
                    drained += card.values[0];
                    card.values[0] = 0;
                }
            }
            state.invalidate_values();
            if drained != 0 && source.zone == ZONE_TABLE {
                let side = side_mut(state, source.side);
                if let Some(card) = side.table.get_mut(source.index) {
                    for index in card.scope_indices(SCOPE_ALL) {
                        if card.has_type(index, TYPE_DRAIN) {
                            let value = card.values[index] + drained;
                            card.values[index] = if card.has_type(index, TYPE_BROKEN) {
                                value.min(0)
                            } else {
                                value.max(0)
                            };
                        }
                    }
                }
            }
        }
        OP_HOLLOW => {
            let spots = gather_targets(state, source, effect);
            for_each_table_card(state, &spots, |card| {
                card.flags |= crate::model::FLAG_HOLLOW;
            });
        }
        OP_DISCARD => {
            let spots = gather_targets(state, source, effect);
            // Deepest first keeps the remaining indices valid while removing.
            for &spot in spots.iter().rev() {
                discard(state, spot);
            }
        }
        OP_EXHAUST => {
            let spots = gather_targets(state, source, effect);
            for &spot in spots.iter().rev() {
                take_card(state, spot);
            }
        }
        OP_MOVE => {
            // `a`: 1 top of draw pile, 2 bottom, 3 discard, 4 sleeve, 5 table;
            // `b` is the destination owner selector (EOwner bits).
            let dest = effect.a as u8;
            let Some(side) = owner_side(source.side, effect.b) else {
                return;
            };
            let spots = gather_targets(state, source, effect);
            for &spot in spots.iter().rev() {
                let Some(card) = take_card(state, spot) else {
                    continue;
                };
                match dest {
                    1 => push_draw(state, side, 0, card),
                    2 => push_draw(state, side, usize::MAX, card),
                    3 => side_mut(state, side).discard_count += 1,
                    4 => push_sleeve(state, side, card),
                    _ => {
                        if !push_table(state, side, card, true) {
                            side_mut(state, side).discard_count += 1;
                        }
                    }
                }
            }
        }
        OP_DRAW => {
            // `a` source owner bits, `b` target owner bits, `c` target location bits,
            // `d` card count.
            let Some(&from) = owner_sides(source.side, effect.a).first() else {
                return;
            };
            let Some(&to) = owner_sides(source.side, effect.b).first() else {
                return;
            };
            for _ in 0..effect.d.max(0) {
                if side_ref(state, from).deck_pos as usize >= side_ref(state, from).deck.len() {
                    // The game shuffles the discard pile into an empty deck; that is
                    // random, so drawing simply stops here.
                    break;
                }
                let Some(card) = take_card(state, Spot::draw(from, 0)) else {
                    break;
                };
                if effect.c & LOC_TABLE != 0 {
                    if !push_table(state, to, card, true) {
                        break;
                    }
                } else if effect.c & LOC_SLEEVE != 0 {
                    if to == 1 {
                        break;
                    }
                    push_sleeve(state, to, card);
                } else if effect.c & LOC_DISCARD != 0 {
                    side_mut(state, to).discard_count += 1;
                } else if effect.c & LOC_DRAW != 0 {
                    push_draw(state, to, 0, card);
                } else {
                    break;
                }
            }
        }
        OP_DUPLICATE => {
            // `a`: 1 table, 2 draw pile top, 3 sleeve; `b` is the copy count. The game
            // gates every mode on a free table slot.
            let Some(original) = card_at(state, source) else {
                return;
            };
            if source.side == 1 || side_ref(state, source.side).capacity <= 0 {
                return;
            }
            for _ in 0..effect.b.max(0) {
                match effect.a as u8 {
                    1 => {
                        if !push_table(state, source.side, original, true) {
                            break;
                        }
                    }
                    2 => push_draw(state, source.side, 0, original),
                    3 => push_sleeve(state, 0, original),
                    _ => break,
                }
            }
        }
        OP_EXPLOIT => {
            let other = 1 - source.side;
            let moved = side_ref(state, other).stash.min(effect.a.max(0));
            side_mut(state, other).stash -= moved;
            side_mut(state, other).bet += moved;
        }
        OP_COINS => move_coins(state, source.side, effect.a, effect.b, effect.c),
        OP_SKIP_TURN => {
            // `SkipTurns` is consumed at round start; mid-round skips have no effect.
        }
        OP_TRIGGER => {
            let trigger = effect.a as u8;
            let spots = gather_targets(state, source, effect);
            for spot in spots {
                if spot.zone == ZONE_TABLE {
                    apply_effects_at(state, spot, trigger, depth + 1);
                }
            }
        }
        OP_SWAP => {
            let spots = gather_targets(state, source, effect);
            let Some(&target) = spots.first() else {
                return;
            };
            if source.zone != ZONE_TABLE
                || target.zone != ZONE_TABLE
                || (source.side == target.side && source.index == target.index)
            {
                return;
            }
            let (Some(a), Some(b)) = (card_at(state, source), card_at(state, target)) else {
                return;
            };
            if let Some(card) = side_mut(state, source.side).table.get_mut(source.index) {
                *card = b;
            }
            if let Some(card) = side_mut(state, target.side).table.get_mut(target.index) {
                *card = a;
            }
            state.invalidate_values();
        }
        OP_INSIGHT => {
            // The opponent peeks at the top of its own deck; the player already sees every
            // card and reorders the pile through the plugin, so only side 1 is affected.
            if source.side == 1 {
                state.insight_left = state.insight_left.saturating_add(effect.a.max(0));
            }
        }
        OP_IGNITE => {
            // `Ignite` adds an IgniteModifier; the second one burns the card, exactly like
            // `ExhaustGroupEffect` does in `IgniteModifier.Apply`.
            let spots = gather_targets(state, source, effect);
            for &spot in spots.iter().rev() {
                if spot.zone != ZONE_TABLE {
                    continue;
                }
                let burned = {
                    let side = side_mut(state, spot.side);
                    match side.table.get_mut(spot.index) {
                        Some(card) => {
                            card.ignited = card.ignited.saturating_add(1).min(2);
                            card.ignited >= 2
                        }
                        None => false,
                    }
                };
                if burned {
                    take_card(state, spot);
                }
            }
            state.invalidate_values();
        }
        _ => {}
    }
}

/// Apply every effect of the card at `spot` whose trigger matches, in card order.
/// The opponent activates optional containers unless the executor has blackjack and
/// the container opts out, exactly like `OpponentController`.
pub fn apply_effects_at(state: &mut State, spot: Spot, trigger: u8, depth: u8) {
    if depth > 3 || state.effect_blocks.is_empty() {
        return;
    }
    let Some(card) = card_at(state, spot) else {
        return;
    };
    if !card.has_effects() {
        return;
    }
    let blocks = state.effect_blocks.clone();
    let executor = spot.side;
    for effect in card.effects(&blocks).iter().copied() {
        if effect.trigger != trigger || effect.op == OP_NONE {
            continue;
        }
        if !condition_ok(state, &effect, executor) {
            continue;
        }
        if executor == 1
            && effect.flags & crate::model::EF_SUPPRESS_ON_BLACKJACK != 0
            && state.o_bj()
        {
            continue;
        }
        apply_effect(state, spot, &effect, depth);
    }
}

/// Fire `trigger` for every table card, side 0 then side 1 in placement order,
/// mirroring `CardEffectController`'s resolution sweep.
pub fn apply_trigger(state: &mut State, trigger: u8) {
    for side in 0..2 {
        let len = side_ref(state, side).table.len();
        for index in 0..len {
            apply_effects_at(state, Spot::table(side, index), trigger, 0);
        }
    }
}

/// A card has just been placed on `side`'s table. `CardPlayedToTable` reactions of
/// the cards already on the table run before the played card's own `Play` effects,
/// matching the `QueueExecutionTiming::CardReaction` priority.
pub fn apply_play(state: &mut State, side: usize) {
    if state.effect_blocks.is_empty() {
        return;
    }
    // Effects only run from or react to table cards; skip the shared table clone unless
    // at least one is present.
    let table_has_effects = |table: &crate::model::CardList| table.iter().any(Card::has_effects);
    if !table_has_effects(&state.p.table) && !table_has_effects(&state.o.table) {
        return;
    }
    let blocks = state.effect_blocks.clone();
    for reaction_side in 0..2 {
        let len = side_ref(state, reaction_side).table.len();
        for index in 0..len {
            if reaction_side == side && index + 1 == len {
                continue;
            }
            let Some(card) = card_at(state, Spot::table(reaction_side, index)) else {
                continue;
            };
            if !card.has_effects() {
                continue;
            }
            for effect in card.effects(&blocks).iter().copied() {
                if effect.trigger != crate::model::TRIGGER_CARD_PLAYED_TO_TABLE
                    || !condition_ok(state, &effect, reaction_side)
                {
                    continue;
                }
                let same_table = reaction_side == side;
                let wanted = if same_table {
                    crate::model::EF_REACT_SAME_TABLE
                } else {
                    crate::model::EF_REACT_OTHER_TABLE
                };
                if effect.flags & wanted == 0 {
                    continue;
                }
                apply_effect(state, Spot::table(reaction_side, index), &effect, 0);
            }
        }
    }
    let len = side_ref(state, side).table.len();
    if len > 0 {
        apply_effects_at(
            state,
            Spot::table(side, len - 1),
            crate::model::TRIGGER_PLAY,
            0,
        );
    }
}
