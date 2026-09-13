//! C ABI entry points.
//!
//! The managed plugin loads this library explicitly (it ships next to the plugin DLL)
//! and calls the exported functions on its solver worker thread, so nothing here may
//! touch Unity APIs. Every entry point catches panics and reports them as `-100`
//! instead of letting the unwind cross the FFI boundary - a panic there would take
//! the whole game down.
//!
//! Buffer protocol: the caller passes an input buffer and a pre-allocated output
//! buffer. The return value is `0` on success, `1` when the output buffer was too
//! small (the required size is written to `out_len`), or a negative [`protocol`]
//! error code. `out_len` receives the number of bytes written.

pub mod batch;
pub mod effects;
pub mod model;
pub mod protocol;
pub mod solver;
pub mod total;

use std::panic::{AssertUnwindSafe, catch_unwind};

use crate::protocol::{
    ERR_TRUNCATED, Reader, VERSION, Writer, parse_candidate, parse_state, write_batch_result,
    write_evaluate_result, write_solve_result,
};
use crate::solver::Budget;

const ERR_PANIC: i32 = -100;
const STATUS_TOO_SMALL: i32 = 1;
const MAX_BATCH_CANDIDATES: usize = 4096;

#[unsafe(no_mangle)]
pub extern "C" fn opl_version() -> u32 {
    VERSION
}

#[unsafe(no_mangle)]
pub extern "C" fn opl_solve(
    input: *const u8,
    input_len: usize,
    node_budget: u64,
    time_ms: u64,
    output: *mut u8,
    output_cap: usize,
    out_len: *mut usize,
) -> i32 {
    dispatch(input, input_len, output, output_cap, out_len, |reader| {
        let state = parse_state(reader)?;
        let outcome = solver::solve(&state, budget(node_budget, time_ms));
        let mut writer = Writer::new();
        write_solve_result(&mut writer, &outcome);
        Ok(writer.into_vec())
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn opl_evaluate(
    input: *const u8,
    input_len: usize,
    node_budget: u64,
    time_ms: u64,
    output: *mut u8,
    output_cap: usize,
    out_len: *mut usize,
) -> i32 {
    dispatch(input, input_len, output, output_cap, out_len, |reader| {
        let state = parse_state(reader)?;
        let value = solver::evaluate(state, budget(node_budget, time_ms));
        let mut writer = Writer::new();
        write_evaluate_result(&mut writer, value);
        Ok(writer.into_vec())
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn opl_evaluate_batch(
    input: *const u8,
    input_len: usize,
    node_budget: u64,
    time_ms: u64,
    output: *mut u8,
    output_cap: usize,
    out_len: *mut usize,
) -> i32 {
    dispatch(input, input_len, output, output_cap, out_len, |reader| {
        let base = parse_state(reader)?;
        let count = reader.u32()? as usize;
        if count > MAX_BATCH_CANDIDATES {
            return Err(ERR_TRUNCATED);
        }
        let mut candidates = Vec::with_capacity(count);
        for _ in 0..count {
            candidates.push(parse_candidate(reader)?);
        }
        let values = batch::evaluate_candidates(&base, &candidates, budget(node_budget, time_ms))?;
        let mut writer = Writer::new();
        write_batch_result(&mut writer, &values);
        Ok(writer.into_vec())
    })
}

fn budget(nodes: u64, time_ms: u64) -> Budget {
    // Clamp so a bogus config cannot pin the worker thread for minutes.
    Budget::new(nodes.clamp(1, 100_000_000), time_ms.clamp(1, 60_000))
}

fn dispatch<F>(
    input: *const u8,
    input_len: usize,
    output: *mut u8,
    output_cap: usize,
    out_len: *mut usize,
    run: F,
) -> i32
where
    F: FnOnce(&mut Reader) -> Result<Vec<u8>, i32>,
{
    if input.is_null() || output.is_null() || out_len.is_null() {
        return ERR_TRUNCATED;
    }

    let input = unsafe { std::slice::from_raw_parts(input, input_len) };
    let result = catch_unwind(AssertUnwindSafe(|| run(&mut Reader::new(input))));

    match result {
        Ok(Ok(bytes)) => {
            if bytes.len() > output_cap {
                unsafe { *out_len = bytes.len() };
                return STATUS_TOO_SMALL;
            }
            unsafe {
                std::ptr::copy_nonoverlapping(bytes.as_ptr(), output, bytes.len());
                *out_len = bytes.len();
            }
            0
        }
        Ok(Err(code)) => {
            unsafe { *out_len = 0 };
            code
        }
        Err(_) => {
            unsafe { *out_len = 0 };
            ERR_PANIC
        }
    }
}
