//! Table value arithmetic, ported 1:1 from the game (`FindOptimalResult`) and the
//! managed solver: the best sum that does not exceed the target, or - if every
//! combination busts - the smallest sum above it.

use smallvec::SmallVec;

use crate::model::Card;

/// Inline capacity for the sum sets. A full table is a handful of cards with one or two
/// values each, so even a peeked card stays far below this; larger tables spill to the
/// heap instead of failing.
type Sums = SmallVec<[i32; 512]>;

pub fn total(cards: &[Card], target: i32) -> i32 {
    let mut sums = Sums::new();
    let mut next = Sums::new();
    sums.push(0);

    for card in cards {
        next.clear();
        for &sum in &sums {
            for &value in card.values() {
                next.push(sum + value);
            }
        }
        next.sort_unstable();
        next.dedup();
        std::mem::swap(&mut sums, &mut next);
    }

    let mut best_below = i32::MIN;
    let mut min_above = i32::MAX;
    for &sum in &sums {
        if sum <= target {
            best_below = best_below.max(sum);
        } else {
            min_above = min_above.min(sum);
        }
    }
    if best_below != i32::MIN {
        best_below
    } else {
        min_above
    }
}

/// Two cards, one of them an ace, the other a ten: a blackjack beats everything.
pub fn has_blackjack(cards: &[Card]) -> bool {
    if cards.len() != 2 {
        return false;
    }
    let (a, b) = (&cards[0], &cards[1]);
    (a.is_ace() && b.has_value(10)) || (b.is_ace() && a.has_value(10))
}

/// Highest single card value on a table, used for the equal-score tiebreak.
pub fn highest(cards: &[Card]) -> i32 {
    cards.iter().map(Card::highest).max().unwrap_or(i32::MIN)
}
