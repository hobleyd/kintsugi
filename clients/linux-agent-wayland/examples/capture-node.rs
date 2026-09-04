//! Streams one PipeWire node with the real capture code, bypassing the portal.
//!
//! # What this is for
//!
//! The PipeWire half of this crate is the part that cannot be checked by reading it. The format pod
//! is negotiated with whatever is producing frames, the buffers may or may not arrive mapped, and
//! the stride is a property of each buffer rather than of the format — three things that all fail
//! quietly, as a stream that connects and delivers nothing.
//!
//! A compositor is not needed to exercise any of that, because none of it is compositor-specific:
//! any PipeWire video source negotiates the same way. So this connects to PipeWire directly, streams
//! the node id given on the command line, and writes exactly the bytes the agent would receive.
//!
//! ```text
//! # in one shell, publish a test pattern
//! gst-launch-1.0 videotestsrc ! video/x-raw,format=BGRx,width=640,height=480 ! pipewiresink
//! # find its node id, then
//! cargo run --example capture-node -- <node-id> > frames.bin
//! ```
//!
//! An example rather than a flag on the binary: a `--node-id` switch on the shipped helper would be
//! a way to point it at a stream the portal never granted, which is precisely the check this crate
//! exists to go through. `cargo build --release` does not build examples, so this cannot ship.

use anyhow::{Context, Result};
use pipewire as pw;

fn main() -> Result<()> {
    let node_id: u32 = std::env::args()
        .nth(1)
        .context("usage: capture-node <pipewire-node-id>")?
        .parse()
        .context("the node id should be a number")?;

    pw::init();

    let mainloop = pw::main_loop::MainLoopRc::new(None).context("creating the PipeWire main loop")?;
    let context = pw::context::ContextRc::new(&mainloop, None).context("creating a PipeWire context")?;
    // The one difference from the shipped path: an ordinary connection rather than the portal's
    // file descriptor. Everything after this point is the same code.
    let core = context.connect_rc(None).context("connecting to PipeWire")?;

    kintsugi_agent_wayland::capture::stream_node(&mainloop, core, node_id, false)
}
