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

/// A card in the solver's model. Equal cards are interchangeable everywhere.
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Card {
    /// Current values, padded with zeroes to [`MAX_VALUES`].
    pub values: [i32; MAX_VALUES],
    pub value_count: u8,
    pub flags: u8,
}

impl Card {
    pub fn new(values: &[i32], flags: u8) -> Self {
        debug_assert!(!values.is_empty() && values.len() <= MAX_VALUES);
        let mut padded = [0i32; MAX_VALUES];
        padded[..values.len()].copy_from_slice(values);
        Card {
            values: padded,
            value_count: values.len() as u8,
            flags,
        }
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
    pub sleeve_size: i32,
    pub sleeve_costs: Vec<i32>,
    pub payable: i32,
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
            sleeve_size: 0,
            sleeve_costs: Vec::new(),
            payable: 0,
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
}

impl Default for State {
    fn default() -> Self {
        Self::new()
    }
}
