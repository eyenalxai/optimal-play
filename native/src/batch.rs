//! Parallel evaluation of independent candidate positions.
//!
//! The selection dialogs (insight, demand, card choice, shuffle) each produce a list of
//! state variants; every variant is scored on its own, which makes them a perfect fit
//! for rayon. The main draw-phase search stays single-threaded: it is one memoized DFS
//! where sharing the transposition table matters more than splitting the root.

use std::sync::OnceLock;

use rayon::prelude::*;
use rayon::{ThreadPool, ThreadPoolBuilder};

use crate::model::State;
use crate::protocol::{Op, apply_ops};
use crate::solver::{Budget, evaluate};

/// One long-lived pool so candidate searches run on threads with the same generous
/// stack as the main solve instead of rayon's default.
fn pool() -> &'static ThreadPool {
    static POOL: OnceLock<ThreadPool> = OnceLock::new();
    POOL.get_or_init(|| {
        ThreadPoolBuilder::new()
            .thread_name(|index| format!("opl-batch-{index}"))
            .stack_size(crate::SOLVER_STACK)
            .build()
            .expect("rayon pool")
    })
}

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

    Ok(pool().install(|| {
        states
            .into_par_iter()
            .map(|state| evaluate(state, budget))
            .collect()
    }))
}
