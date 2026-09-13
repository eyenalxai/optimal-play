//! Parallel evaluation of independent candidate positions.
//!
//! The selection dialogs (insight, demand, card choice, shuffle) each produce a list of
//! state variants; every variant is scored on its own, which makes them a perfect fit
//! for rayon. The main draw-phase search stays single-threaded: it is one memoized DFS
//! where sharing the transposition table matters more than splitting the root.

use rayon::prelude::*;

use crate::model::State;
use crate::protocol::{Op, apply_ops};
use crate::solver::{Budget, evaluate};

pub fn evaluate_candidates(
    base: &State,
    candidates: &[Vec<Op>],
    budget: Budget,
) -> Result<Vec<f32>, i32> {
    // Clone sequentially (a few kilobytes per candidate) so the parallel part only
    // works on owned states; `State` is `Send` but not `Sync` because of its value cache.
    let states: Vec<State> = candidates
        .iter()
        .map(|ops| {
            let mut state = base.clone();
            apply_ops(&mut state, ops)?;
            Ok(state)
        })
        .collect::<Result<_, i32>>()?;

    Ok(states
        .into_par_iter()
        .map(|state| evaluate(state, budget))
        .collect())
}
