//! The Kintsugi agent's Wayland backend, as a library so the pieces can be exercised in isolation.
//!
//! `main.rs` is the shipped binary and is a thin wiring of these three modules. The split exists
//! because the PipeWire half is the part that cannot be checked by inspection — see
//! `examples/capture-node.rs`, which runs the real capture against a real PipeWire producer without
//! a portal or a compositor in the way.

pub mod capture;
pub mod portal;
pub mod wire;
