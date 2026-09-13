//! The solver's reduced view of a round.
//!
//! A [`Card`] carries only what changes the course of a round: its current values
//! (one entry for a normal card, several for e.g. an ace or an awakened Heart) and
//! the boolean game flags the search looks at. Everything else (names, art, effects)
//! stays on the managed side and never crosses the FFI boundary.

use std::cell::Cell;
use std::sync::Arc;

use smallvec::SmallVec;

/// Sleeves are at most a handful of cards and tables a handful of slots, so the search
/// clones them constantly without touching the heap.
pub type CardList = SmallVec<[Card; 8]>;

/// Longest value list a card may have. The game's cards have one or two values.
pub const MAX_VALUES: usize = 8;

pub const FLAG_ACE: u8 = 1 << 0;
pub const FLAG_HOLLOW: u8 = 1 << 1;
pub const FLAG_ALWAYS_INSIGHT: u8 = 1 << 2;
pub const FLAG_CAN_PLAY_OPPONENT: u8 = 1 << 3;
pub const FLAG_BROKEN: u8 = 1 << 4;

/// `ModifiableValue.EType` bits that matter for effects.
pub const TYPE_TARGET: u8 = 1;
pub const TYPE_DRAIN: u8 = 4;
pub const TYPE_BROKEN: u8 = 8;

/// `CardEffectsTrigger` values.
pub const TRIGGER_PLAY: u8 = 10;
pub const TRIGGER_RESOLUTION_BEFORE: u8 = 20;
pub const TRIGGER_RESOLUTION_AFTER: u8 = 21;
pub const TRIGGER_CARD_PLAYED_TO_TABLE: u8 = 60;

/// Effect flags.
pub const EF_OPTIONAL: u8 = 1 << 0;
/// The opponent skips this optional container while it has a blackjack.
pub const EF_SUPPRESS_ON_BLACKJACK: u8 = 1 << 1;
pub const EF_REACT_SAME_TABLE: u8 = 1 << 2;
pub const EF_REACT_OTHER_TABLE: u8 = 1 << 3;

/// Longest effect list a card may carry. Cards with more effects are reported as
/// unmodeled by the managed mapper instead of being truncated silently.
pub const MAX_EFFECTS: usize = 4;

/// One card effect reduced to what the search needs. The field layout is a flat
/// opcode record; the managed mapper is the only writer.
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Debug, Default)]
pub struct Effect {
    pub trigger: u8,
    pub flags: u8,
    pub op: u8,
    /// Target specification kind (see `effects.rs`).
    pub target: u8,
    /// Target payload: relative-position flags, gather owner/location bits or both.
    pub t1: i32,
    pub t2: i32,
    pub t3: i32,
    /// Op payload; meaning depends on `op`.
    pub a: i32,
    pub b: i32,
    pub c: i32,
    pub d: i32,
    /// Packed `CardFilterSet` subset (kind, take mode, count).
    pub filter: u32,
    pub cond: u8,
    pub cond_cmp: u8,
    pub cond_a: i32,
    pub cond_b: i32,
}

/// A card's effect list. Cards outnumber effect lists by far, so the lists live once in
/// the state's shared table and cards only carry an index; equal lists are interned so
/// equal cards hash equal.
#[derive(Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Debug, Default)]
pub struct EffectBlock {
    pub count: u8,
    pub effects: [Effect; MAX_EFFECTS],
}

impl EffectBlock {
    pub fn slice(&self) -> &[Effect] {
        &self.effects[..self.count as usize]
    }
}

/// A card in the solver's model. Equal cards are interchangeable everywhere.
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Card {
    /// Current values, padded with zeroes to [`MAX_VALUES`].
    pub values: [i32; MAX_VALUES],
    pub value_count: u8,
    pub flags: u8,
    /// IgniteModifier count; a second ignite burns (exhausts) the card.
    pub ignited: u8,
    /// `ModifiableValue.EType` bits per value.
    pub types: [u8; MAX_VALUES],
    /// Index+1 into [`State::effect_blocks`]; 0 means the card has no effects.
    pub effects_ref: u32,
}

impl Card {
    pub fn new(values: &[i32], flags: u8) -> Self {
        debug_assert!(!values.is_empty() && values.len() <= MAX_VALUES);
        let mut padded = [0i32; MAX_VALUES];
        padded[..values.len()].copy_from_slice(values);
        let mut types = [0u8; MAX_VALUES];
        for slot in types.iter_mut().take(values.len()) {
            *slot = TYPE_TARGET;
        }
        Card {
            values: padded,
            value_count: values.len() as u8,
            flags,
            ignited: 0,
            types,
            effects_ref: 0,
        }
    }

    /// The card's effect list from a state's shared table.
    pub fn effects<'a>(&self, blocks: &'a [EffectBlock]) -> &'a [Effect] {
        if self.effects_ref == 0 {
            return &[];
        }
        blocks[(self.effects_ref - 1) as usize].slice()
    }

    pub fn has_effects(&self) -> bool {
        self.effects_ref != 0
    }

    /// Apply a `CardValueTempModifier` adjustment (see `CardValueTempModifier.EAdjustmentType`)
    /// to every value the scope selects.
    pub fn adjust_values(&mut self, scope: i32, delta: i32, adjustment: u8) {
        for index in self.scope_indices(scope) {
            let value = self.values[index];
            let adjusted = match adjustment {
                // Relative: the temp modifier sets `current + delta`.
                0 => value + delta,
                // Absolute: the temp modifier sets `delta`.
                1 => delta,
                // Invert: reads the clamped current value and negates it.
                2 => -value,
                // Break: `-abs(current)`.
                3 => -value.abs(),
                // Mend: `abs(current)`.
                4 => value.abs(),
                _ => value,
            };
            self.values[index] = if self.types[index] & TYPE_BROKEN != 0 {
                adjusted.min(0)
            } else {
                adjusted.max(0)
            };
        }
    }

    /// Indices selected by `CardValueTempModifier.ETargetValues`
    /// (0 All, 1 Highest, 2 Lowest, 3 First).
    pub fn scope_indices(&self, scope: i32) -> SmallVec<[usize; MAX_VALUES]> {
        let count = self.value_count as usize;
        let mut indices: SmallVec<[usize; MAX_VALUES]> = (0..count).collect();
        match scope {
            1 => {
                // Highest: the first value with the maximum (LINQ stability).
                let best = self.values[..count].iter().copied().max().unwrap_or(0);
                indices.retain(|index| self.values[*index] == best);
                indices.truncate(1);
            }
            2 => {
                let best = self.values[..count].iter().copied().min().unwrap_or(0);
                indices.retain(|index| self.values[*index] == best);
                indices.truncate(1);
            }
            3 => indices.truncate(1),
            _ => {}
        }
        indices
    }

    pub fn has_type(&self, index: usize, bit: u8) -> bool {
        self.types[index] & bit != 0
    }

    /// True while any value still carries the Broken type.
    pub fn is_broken(&self) -> bool {
        self.types[..self.value_count as usize]
            .iter()
            .any(|&types| types & TYPE_BROKEN != 0)
    }

    pub fn values(&self) -> &[i32] {
        &self.values[..self.value_count as usize]
    }

    pub fn highest(&self) -> i32 {
        self.values().iter().copied().max().unwrap_or(i32::MIN)
    }

    /// A card whose best possible contribution is zero or less (e.g. an awakened Heart).
    pub fn is_dead(&self) -> bool {
        self.highest() <= 0
    }

    pub fn is_ace(&self) -> bool {
        self.flags & FLAG_ACE != 0
    }

    pub fn is_hollow(&self) -> bool {
        self.flags & FLAG_HOLLOW != 0
    }

    pub fn always_insight(&self) -> bool {
        self.flags & FLAG_ALWAYS_INSIGHT != 0
    }

    pub fn can_play_opponent(&self) -> bool {
        self.flags & FLAG_CAN_PLAY_OPPONENT != 0
    }

    pub fn has_value(&self, value: i32) -> bool {
        self.values().contains(&value)
    }
}

#[derive(Clone)]
pub struct Side {
    /// Draw pile in draw order: `deck[deck_pos]` is the next card. Shared through an
    /// `Arc` exactly like the managed side shared its `List` reference: the search only
    /// ever moves `deck_pos`, so cloning a state must not copy the whole pile.
    pub deck: Arc<[Card]>,
    /// Only the size of the discard pile matters to the search (reshuffle check).
    pub discard_count: u32,
    pub deck_pos: u32,
    /// Sleeved cards in sleeve order; the oldest is played/discarded first.
    pub sleeve: CardList,
    /// Cards on the table, in placement order.
    pub table: CardList,
    pub passed: bool,
    pub out_of_cards: bool,
    pub capacity: i32,
    pub bet: i32,
    /// Coins in this side's stash (value, soul coins count 5). Effects move coins
    /// between stash, bet and the winners pot.
    pub stash: i32,
    pub sleeve_draws: u32,
}

impl Side {
    pub fn new() -> Self {
        Side {
            deck: Arc::from(Vec::new()),
            discard_count: 0,
            deck_pos: 0,
            sleeve: CardList::new(),
            table: CardList::new(),
            passed: false,
            out_of_cards: false,
            capacity: 0,
            bet: 0,
            stash: 0,
            sleeve_draws: 0,
        }
    }

    pub fn top(&self) -> Option<&Card> {
        self.deck.get(self.deck_pos as usize)
    }

    /// The game reshuffles the discard pile once the draw pile runs out; until then
    /// at least one of the two piles can be drawn from.
    pub fn has_drawable_cards(&self) -> bool {
        (self.deck_pos as usize) < self.deck.len() || self.discard_count > 0
    }
}

impl Default for Side {
    fn default() -> Self {
        Self::new()
    }
}

#[derive(Clone)]
pub struct State {
    pub plain_target: i32,
    pub p_mods: i32,
    pub o_mods: i32,
    pub holds_at: i32,
    pub insight_left: i32,
    /// The round's Blind value, compared against by `Blind` activation conditions.
    pub blind: i32,
    pub sleeve_size: i32,
    /// Shared with every cloned state: the cost table is immutable for a whole search.
    pub sleeve_costs: Arc<[i32]>,
    /// Shared effect lists referenced by [`Card::effects_ref`]; immutable per search.
    pub effect_blocks: Arc<[EffectBlock]>,
    pub payable: i32,
    /// Winners pot value; the player may pay sleeve costs from it when configured.
    pub pot: i32,
    /// True when the captured configuration lets sleeve costs draw on the winners pot too.
    pub pot_usable: bool,
    pub uprising: bool,
    pub supper: bool,
    pub p: Side,
    pub o: Side,

    // Recomputed table values are the hot path, so they are cached exactly like the
    // managed solver cached them. Mutations invalidate both.
    p_value: Cell<Option<i32>>,
    o_value: Cell<Option<i32>>,
}

impl State {
    pub fn new() -> Self {
        State {
            plain_target: 0,
            p_mods: 0,
            o_mods: 0,
            holds_at: 0,
            insight_left: 0,
            blind: 0,
            sleeve_size: 0,
            sleeve_costs: Arc::from([]),
            effect_blocks: Arc::from([]),
            payable: 0,
            pot: 0,
            pot_usable: false,
            uprising: false,
            supper: false,
            p: Side::new(),
            o: Side::new(),
            p_value: Cell::new(None),
            o_value: Cell::new(None),
        }
    }

    pub fn p_target(&self) -> i32 {
        self.plain_target + self.p_mods - self.uprising_reduction(&self.p)
    }

    pub fn o_target(&self) -> i32 {
        self.plain_target + self.o_mods - self.uprising_reduction(&self.o)
    }

    fn uprising_reduction(&self, side: &Side) -> i32 {
        if self.uprising { side.capacity * 2 } else { 0 }
    }

    pub fn p_value(&self) -> i32 {
        if let Some(value) = self.p_value.get() {
            return value;
        }
        let value = crate::total::total(&self.p.table, self.p_target()) + self.p_mods;
        self.p_value.set(Some(value));
        value
    }

    pub fn o_value(&self) -> i32 {
        if let Some(value) = self.o_value.get() {
            return value;
        }
        let value = crate::total::total(&self.o.table, self.o_target()) + self.o_mods;
        self.o_value.set(Some(value));
        value
    }

    pub fn p_busted(&self) -> bool {
        self.p_value() > self.plain_target
    }

    pub fn o_busted(&self) -> bool {
        self.o_value() > self.plain_target
    }

    pub fn p_bj(&self) -> bool {
        crate::total::has_blackjack(&self.p.table)
    }

    pub fn o_bj(&self) -> bool {
        crate::total::has_blackjack(&self.o.table)
    }

    pub fn invalidate_values(&self) {
        self.p_value.set(None);
        self.o_value.set(None);
    }

    pub fn sleeve_cost(&self) -> i32 {
        if self.sleeve_costs.is_empty() {
            return i32::MAX;
        }
        let index = (self.p.sleeve_draws as usize).min(self.sleeve_costs.len() - 1);
        self.sleeve_costs[index]
    }

    /// Recompute the payable total after coins moved between stash and winners pot.
    pub fn sync_payable(&mut self) {
        self.payable = self.p.stash + if self.pot_usable { self.pot } else { 0 };
    }
}

impl Default for State {
    fn default() -> Self {
        Self::new()
    }
}
