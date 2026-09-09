//! The Kintsugi agent's Wayland screen-capture and input backend.
//!
//! A short-lived helper the Linux agent starts for the duration of one remote-control session and
//! kills at the end of it. It negotiates an xdg-desktop-portal session, streams raw frames to the
//! agent on stdout and takes input events on stdin. It holds no identity, makes no network call and
//! knows nothing about consent, sessions or the fleet — see `wire.rs` for why that split is the
//! point rather than an accident.
//!
//! # Three threads, and which one has to be the main one
//!
//! PipeWire's main loop must run on the thread that created it, and it never returns until the
//! stream ends, so it takes the main thread. Everything else works around that:
//!
//! - **Main**: the portal negotiation (briefly, on a blocking executor), then PipeWire until the end.
//! - **Portal**: holds the session alive and issues input calls. It exists because every `notify_*`
//!   call is async and the proxies are cheapest to keep on one thread, and because the session must
//!   outlive the negotiation — dropping it closes the compositor's grant.
//! - **stdin**: reads input events and hands them to the portal thread, so a stalled portal call
//!   cannot wedge the reader and lose the queued events behind it.
//!
//! The portal thread is *not* the main thread even though it goes first, because it must still be
//! running while PipeWire is.
//!
//! # Exit
//!
//! There is deliberately no shutdown protocol. The agent kills this process when the session ends,
//! for the same reason the Windows service terminates its session helper rather than negotiating
//! with it: this process holds no lock, writes no file the fleet depends on, and is mid-way through
//! nothing but a screen capture. Closing stdout is the other exit — the frame writer fails, which
//! quits the PipeWire loop, which returns from `main`.

use std::io::BufRead;
use std::sync::mpsc;

use anyhow::{Context, Result};

use kintsugi_agent_wayland::portal::PortalSession;
use kintsugi_agent_wayland::wire::{self, DisplaysMessage, InputMessage};
use kintsugi_agent_wayland::capture;

fn main() {
    if let Err(error) = run() {
        // Reported two ways on purpose. stderr is where the agent's own log picks it up, and the
        // framed error is what reaches the administrator's screen as the reason the session did not
        // start — without it a failure here is a viewer that waits for a frame forever.
        eprintln!("remote control: {error:#}");
        let mut stdout = std::io::stdout();
        let _ = wire::write_error(&mut stdout, &format!("{error:#}"));
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    let (ready_tx, ready_rx) = mpsc::channel::<Result<Ready>>();
    let (input_tx, input_rx) = mpsc::channel::<InputMessage>();

    // PipeWire's own channel rather than an `mpsc`, because the receiving end has to be a callback
    // attached to the capture loop: a stream may only be replaced from the thread driving it, and
    // that is the only way to reach that thread from this one. See `capture::run`.
    let (switch_tx, switch_rx) = pipewire::channel::channel::<u32>();

    // The portal thread. It owns the session for the whole run: returning from this closure would
    // drop it and revoke the compositor's grant mid-session.
    std::thread::Builder::new()
        .name("portal".to_string())
        .spawn(move || pollster::block_on(serve_portal(ready_tx, input_rx)))
        .context("starting the portal thread")?;

    let ready = ready_rx
        .recv()
        .context("the portal thread stopped before reporting whether it had a session")??;

    // Before the first frame, so the agent can offer the picker from the moment the session opens
    // rather than after something has been captured.
    wire::write_displays(&mut std::io::stdout(), &ready.displays)
        .context("reporting the displays the portal granted")?;

    // Started only once there is a session, so nothing is read from stdin that could not yet be
    // acted on — and so a failed negotiation leaves no thread behind.
    std::thread::Builder::new()
        .name("stdin".to_string())
        .spawn(move || read_input(&input_tx, &switch_tx))
        .context("starting the stdin thread")?;

    capture::run(ready.pipewire_fd, ready.node_id, ready.can_control_input, switch_rx)
}

/// What the portal thread reports back once the session is up.
struct Ready {
    pipewire_fd: std::os::fd::OwnedFd,
    node_id: u32,
    can_control_input: bool,
    displays: DisplaysMessage,
}

async fn serve_portal(ready: mpsc::Sender<Result<Ready>>, input: mpsc::Receiver<InputMessage>) {
    let mut session = match PortalSession::negotiate().await {
        Ok(session) => session,
        Err(error) => {
            let _ = ready.send(Err(error));
            return;
        }
    };

    let fd = match session.take_pipewire_fd() {
        Ok(fd) => fd,
        Err(error) => {
            let _ = ready.send(Err(error));
            return;
        }
    };

    let announcement = Ready {
        pipewire_fd: fd,
        node_id: session.node_id,
        can_control_input: session.can_control_input(),
        displays: session.displays(),
    };

    if ready.send(Ok(announcement)).is_err() {
        // Nobody is listening, so `run` has already returned. The session goes with this function.
        return;
    }

    // A blocking receive on an async thread, which is fine and deliberate: this thread does nothing
    // else, and the alternative — an async channel — would pull in a runtime for one queue.
    while let Ok(message) = input.recv() {
        // Not an input event, and handled before `inject` sees it: it moves the node every
        // subsequent pointer position is expressed within, which is the portal's own rule — it will
        // only position a pointer inside a stream belonging to this session.
        if let InputMessage::SelectDisplay { node_id } = message {
            if !session.select_node(node_id) {
                eprintln!(
                    "remote control: ignoring a request to watch PipeWire node {node_id}, which this \
                     portal session was not granted"
                );
            }
            continue;
        }

        if let Err(error) = session.inject(&message).await {
            // Logged and continued. One rejected event is not a reason to end a session: the portal
            // rejects an occasional call during a compositor's own transitions (a workspace switch,
            // a monitor waking), and giving up would end the session over a recoverable hiccup.
            eprintln!("remote control: the portal rejected an input event ({error:#})");
        }
    }
}

/// Reads newline-delimited input events until the agent closes stdin.
///
/// A display switch goes to **both** ends, and both are needed: the portal thread has to move the
/// stream every pointer position is expressed within, and the capture loop has to link to the new
/// node. Sent on the portal channel first, so an event that arrives immediately after the switch is
/// positioned inside the display it was meant for.
fn read_input(input: &mpsc::Sender<InputMessage>, switch: &pipewire::channel::Sender<u32>) {
    let stdin = std::io::stdin();

    for line in stdin.lock().lines() {
        let Ok(line) = line else {
            return;
        };
        if line.trim().is_empty() {
            continue;
        }

        match serde_json::from_str::<InputMessage>(&line) {
            // Dropped rather than fatal, in both directions: an event this build does not
            // understand comes from a newer agent, and ending the session over it would make an
            // unrecognised key a disconnection.
            Err(error) => eprintln!("remote control: could not read an input event ({error})"),
            Ok(message) => {
                let switch_to = match message {
                    InputMessage::SelectDisplay { node_id } => Some(node_id),
                    _ => None,
                };

                if input.send(message).is_err() {
                    return;
                }

                if let Some(node_id) = switch_to {
                    // A failed send means the capture loop has already finished, which ends the
                    // session anyway — there is nothing useful to do about it here.
                    let _ = switch.send(node_id);
                }
            }
        }
    }
}
