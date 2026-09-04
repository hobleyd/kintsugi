//! Remote control: the standing socket that asks this Mac's user for consent, and the session that
//! follows if they give it.
//!
//! # Why this lives in the per-user process
//!
//! macOS is the only one of the three agents where the per-user half holds the fleet identity, and
//! that is exactly what remote control needs: the screen and the keyboard belong to a logged-in
//! GUI session, and the root daemon has neither. On Windows and Linux the per-user process holds no
//! identity and goes through a queue, so remote control there will need a different shape — see the
//! table in CLAUDE.md.
//!
//! A consequence worth stating plainly: **a Mac with nobody logged in is unreachable for remote
//! control**, and correctly so. There is no screen to share and nobody to ask.
//!
//! # Two sockets, and why
//!
//! The *control* socket is standing: opened when this process starts and held for its life, so a
//! session request reaches the user in a second rather than at the next hourly check-in. It carries
//! nothing but session negotiation.
//!
//! A *session* socket is opened per session and carries screen frames and input. Keeping them apart
//! means a frame stream cannot queue up behind a control message, and a session socket dropping
//! mid-call does not cost this host its reachability.
//!
//! Both are `wss://` presenting this agent's own client certificate, on a path nginx gates with an
//! exact-match client-certificate check — so an unenrolled agent cannot open either, and neither
//! can anything that is not this host.
//!
//! # The consent rule
//!
//! Nothing is captured before the person at the keyboard says yes, silence is a refusal, and the
//! session is visible in the menu bar with a way to end it for as long as it runs. Those three
//! together are the whole justification for a feature that otherwise reads as spyware, so none of
//! them is optional and none of them should be made configurable.

use std::io::ErrorKind;
use std::net::{TcpStream, ToSocketAddrs};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use anyhow::{anyhow, Context, Result};
use tungstenite::client::IntoClientRequest;
use tungstenite::stream::MaybeTlsStream;
use tungstenite::{Connector, Message, WebSocket};

use crate::config::{self, Config};
use crate::dialogs::{self, RemoteControlChoice};
use crate::identity::{self, AgentIdentity};
use crate::input_injection::{self, CGPoint, InputInjector};
use crate::logging;
use crate::remote_protocol::{
    parse_server_message, parse_viewer_input, AgentMessage, ConsentOutcome, ServerMessage, ViewerInput,
};
use crate::screen_capture::{self, FrameEncoder, ScreenCapture, DEFAULT_JPEG_QUALITY, DEFAULT_MAX_IMAGE_WIDTH};
use crate::tray_menu;

/// How long the consent dialog stays up.
///
/// Deliberately shorter than the server's own `RemoteControlDefaults.ConsentTimeout` (90s), so the
/// dialog is what gives up and the answer is a reported `TimedOut` rather than the server inferring
/// one from silence. Keep this the smaller of the two if either changes.
const CONSENT_TIMEOUT: Duration = Duration::from_secs(60);

/// How long the control loop waits between reads on an idle socket. Nothing arrives here for hours
/// at a time, so this only bounds how quickly a session request or a queued outbound message is
/// noticed.
const CONTROL_POLL_INTERVAL: Duration = Duration::from_millis(50);

/// How long a session loop waits for a new frame before going back to check for input. Also the
/// upper bound on input latency, which is why it is small: at 20ms the remote pointer feels
/// attached to the hand moving it.
const FRAME_POLL_INTERVAL: Duration = Duration::from_millis(20);

const CONNECT_TIMEOUT: Duration = Duration::from_secs(15);

/// Reconnect backoff for the control socket. The ceiling is a minute because a host that cannot
/// reach the server is a host nobody can connect to anyway, and hammering a server that is down is
/// what turns one outage into two.
const INITIAL_RECONNECT_BACKOFF: Duration = Duration::from_secs(5);
const MAX_RECONNECT_BACKOFF: Duration = Duration::from_secs(60);

/// How long the session thread waits for its consent answer to actually reach the wire before it
/// opens the session socket.
///
/// This closes a real race rather than smoothing it over. Consent travels on the control socket,
/// which the loop drains on its own tick, so without this the session socket can arrive at the
/// relay *before* the grant that authorises it — and the relay refuses a session it has not seen
/// granted. Retrying the connection would not help: the server accepts the WebSocket upgrade and
/// only then checks, so a refusal is a close frame on a handshake that already succeeded, which
/// tungstenite reports as a perfectly good connection. Waiting for the flush is what makes the
/// ordering true instead of likely.
const CONSENT_FLUSH_TIMEOUT: Duration = Duration::from_secs(5);

/// A retry for the ordinary case of a connection that failed to establish — a moment of packet
/// loss, a proxy restarting. Deliberately *not* the fix for the consent race; see above.
const SESSION_CONNECT_ATTEMPTS: u32 = 2;
const SESSION_CONNECT_RETRY_DELAY: Duration = Duration::from_millis(300);

type Socket = WebSocket<MaybeTlsStream<TcpStream>>;

/// One message for the control loop to send, and optionally a way to be told once it has actually
/// gone. Only the consent answer asks — see [`CONSENT_FLUSH_TIMEOUT`].
struct Outbound {
    message: AgentMessage,
    flushed: Option<Sender<()>>,
}

/// The session currently running, if any — shared between the control loop (which learns from the
/// server that a session should stop) and the session thread (which is the only thing that can
/// stop it).
#[derive(Clone, Default)]
struct ActiveSession {
    inner: Arc<Mutex<Option<ActiveSessionState>>>,
}

struct ActiveSessionState {
    session_id: String,
    stop: Arc<AtomicBool>,
}

impl ActiveSession {
    fn begin(&self, session_id: &str) -> Arc<AtomicBool> {
        let stop = Arc::new(AtomicBool::new(false));

        if let Ok(mut held) = self.inner.lock() {
            *held = Some(ActiveSessionState { session_id: session_id.to_string(), stop: stop.clone() });
        }

        stop
    }

    fn end(&self, session_id: &str) {
        if let Ok(mut held) = self.inner.lock() {
            if held.as_ref().is_some_and(|state| state.session_id == session_id) {
                *held = None;
            }
        }
    }

    /// Asks the named session to stop. Ignores an id that is not the running one, so a stale
    /// instruction about a finished session cannot cut a newer one short.
    fn request_stop(&self, session_id: &str) {
        if let Ok(held) = self.inner.lock() {
            if let Some(state) = held.as_ref() {
                if state.session_id == session_id {
                    state.stop.store(true, Ordering::SeqCst);
                }
            }
        }
    }

    fn is_running(&self) -> bool {
        self.inner.lock().is_ok_and(|held| held.is_some())
    }
}

/// Holds the control socket open for the life of this process, reconnecting when it drops.
///
/// Never returns. Intended to be given a thread of its own by `run_ui_agent`.
pub fn run(config: Config, serial_number: String, end_session_requested: Arc<AtomicBool>) {
    let active = ActiveSession::default();
    let mut backoff = INITIAL_RECONNECT_BACKOFF;

    loop {
        // Re-read from disk every attempt rather than taking an identity once. This process is
        // long-running and starts before the root daemon has necessarily enrolled — the scheduler
        // thread has the same arrangement, and for the same reason.
        let Some(identity) = identity::load(&config::identity_dir()) else {
            logging::warn("remote control is unavailable until the root daemon has enrolled an identity");
            std::thread::sleep(backoff);
            backoff = (backoff * 2).min(MAX_RECONNECT_BACKOFF);
            continue;
        };

        match hold_control_socket(&config, &serial_number, &identity, &active, &end_session_requested) {
            Ok(()) => {
                logging::info("the remote control socket was closed by the server; reconnecting");
                backoff = INITIAL_RECONNECT_BACKOFF;
            }
            Err(err) => {
                // {err:#} for the cause chain, the same reason main.rs's post_with_retry does it:
                // reqwest and tungstenite both report every connection failure with an identical
                // outermost message, and "invalid peer certificate: UnknownIssuer" lives in the
                // chain.
                logging::warn(&format!("the remote control socket failed: {err:#}"));
            }
        }

        std::thread::sleep(backoff);
        backoff = (backoff * 2).min(MAX_RECONNECT_BACKOFF);
    }
}

/// Opens the control socket and services it until it closes.
fn hold_control_socket(
    config: &Config,
    serial_number: &str,
    identity: &AgentIdentity,
    active: &ActiveSession,
    end_session_requested: &Arc<AtomicBool>,
) -> Result<()> {
    let url = config.remote_control_url(serial_number, None);
    let mut socket = connect(&url, identity)?;
    set_nonblocking(&socket)?;

    logging::info(&format!("remote control socket open to {url}"));

    // Outbound messages originate on session threads (the consent answer, a session ending), and a
    // WebSocket has one owner. The queue is what keeps those threads off this socket.
    let (outbound_tx, outbound_rx) = mpsc::channel::<Outbound>();

    queue(
        &mut socket,
        &AgentMessage::Hello {
            agent_version: env!("CARGO_PKG_VERSION").to_string(),
            console_user: std::env::var("USER").ok(),
        },
    )?;

    // Whoever is waiting to hear that their message reached the wire. Signalled only after a
    // *complete* flush, since a partial one means some of it is still buffered here.
    let mut awaiting_flush: Vec<Sender<()>> = Vec::new();

    loop {
        // 1. Anything the server has to say.
        match read_control_message(&mut socket)? {
            ControlRead::Message(text) => {
                handle_server_message(&text, config, serial_number, identity, active, &outbound_tx, end_session_requested);
            }
            ControlRead::Closed => return Ok(()),
            ControlRead::Idle => {}
        }

        // 2. Anything a session thread wants said.
        drain_outbound(&mut socket, &outbound_rx, &mut awaiting_flush)?;

        // 3. Push it, and anything tungstenite still had buffered from a previous iteration —
        //    including the pong it queues in answer to the server's keep-alive ping, which is what
        //    stops nginx and any stateful firewall in between treating this socket as dead.
        if flush(&mut socket)? {
            for waiting in awaiting_flush.drain(..) {
                // The receiver has given up if this fails, which only happens on the timeout path.
                let _ = waiting.send(());
            }
        }

        // 4. The menu bar's End Remote Session, which is not addressed to any particular session —
        //    see the tray handler on why it is a flag.
        if end_session_requested.swap(false, Ordering::SeqCst) {
            stop_running_session(active);
        }

        std::thread::sleep(CONTROL_POLL_INTERVAL);
    }
}

enum ControlRead {
    Message(String),
    Closed,
    Idle,
}

fn read_control_message(socket: &mut Socket) -> Result<ControlRead> {
    match socket.read() {
        Ok(Message::Text(text)) => Ok(ControlRead::Message(text.to_string())),
        Ok(Message::Close(_)) => Ok(ControlRead::Closed),
        // Ping/Pong are answered inside tungstenite; a binary message on this socket is not part of
        // the protocol and is ignored rather than treated as a fault.
        Ok(_) => Ok(ControlRead::Idle),
        Err(err) if is_would_block(&err) => Ok(ControlRead::Idle),
        Err(tungstenite::Error::ConnectionClosed) | Err(tungstenite::Error::AlreadyClosed) => Ok(ControlRead::Closed),
        Err(err) => Err(anyhow!(err).context("reading from the remote control socket")),
    }
}

fn drain_outbound(socket: &mut Socket, outbound: &Receiver<Outbound>, awaiting_flush: &mut Vec<Sender<()>>) -> Result<()> {
    while let Ok(outbound) = outbound.try_recv() {
        queue(socket, &outbound.message)?;
        if let Some(flushed) = outbound.flushed {
            awaiting_flush.push(flushed);
        }
    }

    Ok(())
}

fn stop_running_session(active: &ActiveSession) {
    if let Ok(held) = active.inner.lock() {
        if let Some(state) = held.as_ref() {
            logging::info("ending the remote control session at the host user's request");
            state.stop.store(true, Ordering::SeqCst);
        }
    }
}

fn handle_server_message(
    text: &str,
    config: &Config,
    serial_number: &str,
    identity: &AgentIdentity,
    active: &ActiveSession,
    outbound: &Sender<Outbound>,
    end_session_requested: &Arc<AtomicBool>,
) {
    let message = match parse_server_message(text) {
        Ok(Some(message)) => message,
        Ok(None) => {
            // A newer server mentioning something this build does not know about. Logged and
            // ignored, the same way the server treats an unrecognised message from an agent.
            logging::info("ignoring an unrecognised remote control message from the server");
            return;
        }
        Err(err) => {
            logging::warn(&format!("could not read a remote control message from the server: {err}"));
            return;
        }
    };

    match message {
        ServerMessage::SessionRequested { session_id, requested_by, consent_timeout_seconds } => {
            if active.is_running() {
                // The server allows one session per host, so this should not happen; if it does,
                // refusing is the answer that cannot go wrong. Reported as Denied rather than
                // silently dropped, so the requesting administrator gets an answer.
                logging::warn("refusing a remote control request: a session is already running");
                let _ = outbound.send(Outbound {
                    message: AgentMessage::Consent { session_id, outcome: ConsentOutcome::Denied },
                    flushed: None,
                });
                return;
            }

            let stop = active.begin(&session_id);

            // A thread of its own, because everything below it blocks: the consent dialog is an
            // osascript subprocess and the session that follows runs for as long as somebody is
            // watching. The control socket has to stay readable throughout.
            let config = config.clone();
            let serial_number = serial_number.to_string();
            let identity = identity.clone();
            let active = active.clone();
            let outbound = outbound.clone();
            let end_session_requested = end_session_requested.clone();

            std::thread::spawn(move || {
                // Any click that arrived before this session existed belongs to the last one.
                end_session_requested.store(false, Ordering::SeqCst);

                let outcome = negotiate_and_run_session(
                    &config,
                    &serial_number,
                    &identity,
                    &session_id,
                    &requested_by,
                    consent_timeout_seconds,
                    &stop,
                    &outbound,
                );

                if let Err(err) = outcome {
                    logging::warn(&format!("remote control session {session_id} failed: {err:#}"));
                    let _ = outbound.send(Outbound {
                        message: AgentMessage::SessionEnded {
                            session_id: session_id.clone(),
                            reason: format!("{err:#}"),
                        },
                        flushed: None,
                    });
                }

                active.end(&session_id);
                tray_menu::report_remote_session(None);
            });
        }

        ServerMessage::SessionEnded { session_id, reason } => {
            logging::info(&format!("the server ended remote control session {session_id}: {reason}"));
            active.request_stop(&session_id);
        }
    }
}

/// Asks the user, and runs the session if they agree.
#[allow(clippy::too_many_arguments)]
fn negotiate_and_run_session(
    config: &Config,
    serial_number: &str,
    identity: &AgentIdentity,
    session_id: &str,
    requested_by: &str,
    consent_timeout_seconds: u64,
    stop: &Arc<AtomicBool>,
    outbound: &Sender<Outbound>,
) -> Result<()> {
    // Asked for before the dialog, not after: on a Mac without an MDM PPPC profile this is the one
    // permission macOS will actually grant from a prompt, and getting it out of the way first means
    // the consent dialog can describe a session that will work. Already-granted hosts see nothing.
    if !screen_capture::has_screen_recording_permission() {
        logging::info("requesting the Screen Recording permission before asking the console user");
        screen_capture::request_screen_recording_permission();
    }

    let restrictions = describe_restrictions();
    let timeout = CONSENT_TIMEOUT.as_secs().min(consent_timeout_seconds.max(1));

    let choice = dialogs::confirm_remote_control(requested_by, &restrictions, timeout)
        .context("could not ask the console user for consent")?;

    let outcome = match choice {
        RemoteControlChoice::Allow => ConsentOutcome::Granted,
        RemoteControlChoice::Deny => ConsentOutcome::Denied,
        RemoteControlChoice::TimedOut => ConsentOutcome::TimedOut,
    };

    let (flushed_tx, flushed_rx) = mpsc::channel();
    outbound
        .send(Outbound {
            message: AgentMessage::Consent { session_id: session_id.to_string(), outcome },
            flushed: Some(flushed_tx),
        })
        .map_err(|_| anyhow!("the control socket closed before the consent answer could be sent"))?;

    if outcome != ConsentOutcome::Granted {
        return Ok(());
    }

    // Waited for, not assumed: the relay refuses a session socket for a grant it has not seen yet,
    // and refuses it *after* accepting the upgrade — so the agent would read a healthy connection
    // that immediately closes, with nothing to retry against. See CONSENT_FLUSH_TIMEOUT.
    if flushed_rx.recv_timeout(CONSENT_FLUSH_TIMEOUT).is_err() {
        // Proceeding anyway. The grant may still be in flight, in which case the session socket is
        // refused and the administrator is told the session ended — which is a better outcome than
        // refusing a session the user has just agreed to.
        logging::warn("the consent answer has not reached the server yet; opening the session socket regardless");
    }

    run_session(config, serial_number, identity, session_id, requested_by, stop, outbound)
}

/// What this session will not be able to do, in words the person deciding can act on.
///
/// Said up front rather than discovered mid-call. Both permissions fail *quietly* — capture without
/// Screen Recording produces a desktop with no windows in it, and `CGEventPost` without
/// Accessibility is dropped with no error at all — so without this the session looks broken rather
/// than unpermitted.
fn describe_restrictions() -> Vec<String> {
    let mut restrictions = Vec::new();

    if !screen_capture::has_screen_recording_permission() {
        restrictions.push(
            "Your screen cannot be shared: this agent has not been granted Screen Recording."
                .to_string(),
        );
    }

    if !input_injection::has_accessibility_permission() {
        restrictions.push(
            "Keyboard and mouse control will not work: this agent has not been granted Accessibility."
                .to_string(),
        );
    }

    restrictions
}

/// Captures, streams, and injects until somebody stops it.
fn run_session(
    config: &Config,
    serial_number: &str,
    identity: &AgentIdentity,
    session_id: &str,
    requested_by: &str,
    stop: &Arc<AtomicBool>,
    outbound: &Sender<Outbound>,
) -> Result<()> {
    let capture = ScreenCapture::start(DEFAULT_MAX_IMAGE_WIDTH).context("could not start capturing this Mac's screen")?;
    let geometry = capture.geometry;

    let mut socket = connect_session_socket(config, serial_number, identity, session_id)?;
    set_nonblocking(&socket)?;

    // Only now does anything appear in the menu bar, because only now is anything actually being
    // watched. The counterpart `report_remote_session(None)` is in the spawning thread's own
    // cleanup, so it runs even if this function returns an error.
    tray_menu::report_remote_session(Some(requested_by.to_string()));
    logging::info(&format!("remote control session {session_id} started for {requested_by}"));

    // The viewer needs the geometry before the first tile, or it has nothing to draw into and no
    // way to convert a click back into a screen position.
    let display_info = serde_json::to_string(&geometry.to_display_info()).context("could not describe the display")?;
    // Queued and flushed rather than `send`, for the same reason as the control socket: this one is
    // non-blocking too, and a WouldBlock here would abandon a session before its first frame.
    socket
        .write(Message::text(display_info))
        .map_err(|err| anyhow!("{err}"))
        .context("could not send the display geometry to the viewer")?;

    // None when Accessibility is missing. The session still runs — the administrator can see the
    // screen, which is most of what a support call needs — and the dialog already said so.
    let mut injector = InputInjector::new(CGPoint { x: geometry.origin_x, y: geometry.origin_y });
    if injector.is_none() {
        logging::warn("remote input is unavailable: could not create a CoreGraphics event source");
    }

    let mut encoder = FrameEncoder::new(DEFAULT_JPEG_QUALITY);
    let mut pending_flush = false;
    let started = Instant::now();

    // Counted so the log can distinguish the three ways a viewer sees nothing: ScreenCaptureKit
    // delivered no frame (a permission or stream fault — frames_captured stays 0), frames arrived
    // but nothing left this process (tiles_sent stays 0), or everything was sent and the fault is
    // downstream. Without these all three read as "session started, session ended".
    let mut frames_captured: u64 = 0;
    let mut tiles_sent: u64 = 0;
    let mut bytes_sent: u64 = 0;

    let reason = 'session: loop {
        if stop.load(Ordering::SeqCst) {
            break "the session was ended on the host".to_string();
        }

        match pump_input(&mut socket, injector.as_mut(), &mut encoder) {
            Ok(true) => {}
            Ok(false) => break "the viewer disconnected".to_string(),
            Err(err) => break format!("{err:#}"),
        }

        if pending_flush {
            // The socket is not draining as fast as the screen is changing. Frames keep arriving
            // into the capture slot and keep overwriting each other, so nothing accumulates —
            // the remote end sees a lower frame rate rather than a growing delay.
            std::thread::sleep(FRAME_POLL_INTERVAL);
        } else if let Some(frame) = capture.next_frame(FRAME_POLL_INTERVAL) {
            frames_captured += 1;
            if frames_captured == 1 {
                logging::info(&format!(
                    "remote control session {session_id}: first frame captured after {}ms ({}x{})",
                    started.elapsed().as_millis(),
                    frame.width,
                    frame.height
                ));
            }

            for tile in encoder.encode_changes(&frame) {
                tiles_sent += 1;
                bytes_sent += tile.len() as u64;
                // Labelled, because breaking the inner loop alone would discard the error and go
                // straight back to capturing for a socket that is no longer there.
                if let Err(err) = socket.write(Message::Binary(tile.into())) {
                    break 'session format!("{err:#}");
                }
            }
        }

        match flush(&mut socket) {
            Ok(flushed) => pending_flush = !flushed,
            Err(err) => break format!("{err:#}"),
        }
    };

    if frames_captured == 0 {
        logging::warn(&format!(
            "remote control session {session_id}: ScreenCaptureKit delivered no frames at all in {}s",
            started.elapsed().as_secs()
        ));
    }

    // Explicitly, before anything else unwinds: a session that ended while the remote user happened
    // to be holding Command must not leave this Mac's own owner with Command stuck down. `Drop`
    // does this too, as a backstop for a panic.
    if let Some(injector) = injector.as_mut() {
        injector.release_all();
    }

    capture.stop();
    let _ = socket.close(None);

    logging::info(&format!(
        "remote control session {session_id} ended after {}s: {reason} \
         (frames captured: {frames_captured}, tiles sent: {tiles_sent}, bytes sent: {bytes_sent})",
        started.elapsed().as_secs()
    ));

    let _ = outbound.send(Outbound {
        message: AgentMessage::SessionEnded { session_id: session_id.to_string(), reason },
        flushed: None,
    });

    Ok(())
}

/// Reads and applies everything the viewer has sent. `Ok(false)` means it hung up.
fn pump_input(socket: &mut Socket, mut injector: Option<&mut InputInjector>, encoder: &mut FrameEncoder) -> Result<bool> {
    loop {
        match socket.read() {
            Ok(Message::Text(text)) => {
                if let Some(input) = parse_viewer_input(&text) {
                    match input {
                        // Handled here rather than in the injector, because it is about pictures
                        // rather than input — and changing it forces a full frame, which only the
                        // encoder can do.
                        ViewerInput::Quality { jpeg_quality, .. } => {
                            if let Some(quality) = jpeg_quality {
                                encoder.set_quality(quality);
                            }
                        }
                        other => {
                            if let Some(injector) = injector.as_mut() {
                                injector.apply(&other);
                            }
                        }
                    }
                }
            }
            Ok(Message::Close(_)) => return Ok(false),
            Ok(_) => {}
            Err(err) if is_would_block(&err) => return Ok(true),
            Err(tungstenite::Error::ConnectionClosed) | Err(tungstenite::Error::AlreadyClosed) => return Ok(false),
            Err(err) => return Err(anyhow!(err).context("reading from the remote control session socket")),
        }
    }
}

/// Opens the session socket, retrying past the consent race — see
/// [`SESSION_CONNECT_ATTEMPTS`].
fn connect_session_socket(
    config: &Config,
    serial_number: &str,
    identity: &AgentIdentity,
    session_id: &str,
) -> Result<Socket> {
    let url = config.remote_control_url(serial_number, Some(session_id));
    let mut last_error = None;

    for attempt in 1..=SESSION_CONNECT_ATTEMPTS {
        match connect(&url, identity) {
            Ok(socket) => return Ok(socket),
            Err(err) => {
                logging::warn(&format!(
                    "attempt {attempt}/{SESSION_CONNECT_ATTEMPTS} to open the remote control session socket failed: {err:#}"
                ));
                last_error = Some(err);
            }
        }

        if attempt < SESSION_CONNECT_ATTEMPTS {
            std::thread::sleep(SESSION_CONNECT_RETRY_DELAY);
        }
    }

    Err(last_error.unwrap_or_else(|| anyhow!("could not open the remote control session socket")))
}

/// Opens one `wss://` socket presenting this agent's client certificate.
fn connect(url: &str, identity: &AgentIdentity) -> Result<Socket> {
    let tls = identity::to_rustls_client_config(identity)?;
    let request = url
        .into_client_request()
        .with_context(|| format!("{url} is not a usable WebSocket address"))?;

    let uri = request.uri().clone();
    let host = uri.host().context("the remote control address names no host")?;
    let port = uri.port_u16().unwrap_or(match uri.scheme_str() {
        Some("wss") => 443,
        _ => 80,
    });

    // Connected by hand rather than through tungstenite's own `connect`, for two reasons: this is
    // where the connect timeout can be bounded (a host behind a black-holing firewall would
    // otherwise block this thread until the OS gave up, which can be minutes), and
    // `client_tls_with_config` is the only entry point that accepts a rustls configuration carrying
    // a client certificate.
    let address = (host, port)
        .to_socket_addrs()
        .with_context(|| format!("could not resolve {host}"))?
        .next()
        .with_context(|| format!("{host} resolved to no addresses"))?;

    let stream = TcpStream::connect_timeout(&address, CONNECT_TIMEOUT)
        .with_context(|| format!("could not connect to {address}"))?;

    // Nagle off: this connection sends small messages that matter immediately — a keystroke, a
    // tile — and coalescing them into fuller packets trades exactly the latency a remote session is
    // judged on.
    let _ = stream.set_nodelay(true);

    let (socket, _response) = tungstenite::client_tls_with_config(request, stream, None, Some(Connector::Rustls(tls)))
        .map_err(|err| anyhow!("{err}"))
        .with_context(|| format!("the WebSocket handshake with {url} failed"))?;

    Ok(socket)
}

/// Puts the socket into non-blocking mode.
///
/// Both loops here have to read and write on one socket from one thread — a WebSocket has a single
/// owner, and blocking on a read would mean frames stopped whenever the viewer was quiet, which is
/// most of the time. Non-blocking plus a short sleep is what lets one thread do both.
fn set_nonblocking(socket: &Socket) -> Result<()> {
    match socket.get_ref() {
        MaybeTlsStream::Plain(stream) => stream.set_nonblocking(true).context("could not set the socket non-blocking")?,
        MaybeTlsStream::Rustls(stream) => stream
            .sock
            .set_nonblocking(true)
            .context("could not set the TLS socket non-blocking")?,
        _ => return Err(anyhow!("the remote control socket is of an unexpected kind")),
    }

    Ok(())
}

/// Whether an error is "nothing to read yet" rather than a failure.
///
/// `WouldBlock` arrives two ways and both have to be recognised: straight from the socket, and —
/// with rustls in the way — as tungstenite's own `Io` wrapping it after a partial TLS record. Miss
/// either and an idle socket reads as a broken one, which reconnects in a loop.
fn is_would_block(error: &tungstenite::Error) -> bool {
    match error {
        tungstenite::Error::Io(io) => matches!(io.kind(), ErrorKind::WouldBlock | ErrorKind::Interrupted),
        _ => false,
    }
}

/// Queues a message. Does **not** flush.
///
/// `WebSocket::send` would write and flush in one call, and on a non-blocking socket a flush that
/// cannot complete right now returns `WouldBlock` — which is ordinary, not a failure. Reporting it
/// as one tore the control socket down and reconnected, losing the message and the host's
/// reachability along with it. Queue here; [`flush`] is called once per loop iteration and is where
/// `WouldBlock` is tolerated.
fn queue(socket: &mut Socket, message: &AgentMessage) -> Result<()> {
    let json = serde_json::to_string(message).context("could not serialise a remote control message")?;
    socket
        .write(Message::text(json))
        .map_err(|err| anyhow!("{err}"))
        .context("could not queue a remote control message")
}

/// Pushes whatever is queued. `Ok(false)` means the socket could not take all of it yet, which is
/// not an error — tungstenite keeps the remainder and the next call sends it.
fn flush(socket: &mut Socket) -> Result<bool> {
    match socket.flush() {
        Ok(()) => Ok(true),
        Err(err) if is_would_block(&err) => Ok(false),
        Err(err) => Err(anyhow!(err).context("flushing the remote control socket")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn config_with(api_base_url: &str) -> Config {
        Config { api_base_url: api_base_url.to_string(), enrollment_token: None }
    }

    #[test]
    fn the_control_socket_url_carries_only_the_serial_number() {
        assert_eq!(
            config_with("https://kintsugi.example.com:8443").remote_control_url("C02ABC", None),
            "wss://kintsugi.example.com:8443/api/remote-control?serialNumber=C02ABC"
        );
    }

    #[test]
    fn a_session_socket_url_adds_the_session_id() {
        // Same path, told apart by query string — nginx gates this route on an exact match of one
        // path segment, so a second path would be un-gated.
        assert_eq!(
            config_with("https://kintsugi.example.com:8443").remote_control_url("C02ABC", Some("abc-123")),
            "wss://kintsugi.example.com:8443/api/remote-control?serialNumber=C02ABC&sessionId=abc-123"
        );
    }

    #[test]
    fn https_becomes_wss_and_http_becomes_ws() {
        // tungstenite refuses an http:// address outright rather than assuming, so this rewrite is
        // required rather than cosmetic.
        assert!(config_with("https://host:8443").remote_control_url("S", None).starts_with("wss://"));
        assert!(config_with("http://host:8080").remote_control_url("S", None).starts_with("ws://"));
    }

    #[test]
    fn a_trailing_slash_does_not_double_up() {
        assert_eq!(
            config_with("https://host:8443/").remote_control_url("S", None),
            "wss://host:8443/api/remote-control?serialNumber=S"
        );
    }

    #[test]
    fn the_consent_timeout_is_shorter_than_the_servers_own() {
        // The dialog must be what gives up, so the answer is a reported TimedOut rather than the
        // server inferring one from silence. The server's value is 90s
        // (RemoteControlDefaults.ConsentTimeout).
        assert!(CONSENT_TIMEOUT < Duration::from_secs(90));
    }

    #[test]
    fn an_active_session_reports_itself_and_clears() {
        let active = ActiveSession::default();
        assert!(!active.is_running());

        let stop = active.begin("session-1");
        assert!(active.is_running());
        assert!(!stop.load(Ordering::SeqCst));

        active.end("session-1");
        assert!(!active.is_running());
    }

    #[test]
    fn stopping_names_the_session_so_a_stale_instruction_cannot_cut_a_newer_one_short() {
        let active = ActiveSession::default();
        let stop = active.begin("session-2");

        active.request_stop("session-1");
        assert!(!stop.load(Ordering::SeqCst));

        active.request_stop("session-2");
        assert!(stop.load(Ordering::SeqCst));
    }

    #[test]
    fn ending_a_session_that_is_not_the_running_one_leaves_it_alone() {
        let active = ActiveSession::default();
        active.begin("session-2");

        active.end("session-1");

        assert!(active.is_running());
    }

    #[test]
    fn would_block_is_told_apart_from_a_real_failure() {
        // An idle socket reading as a broken one is a reconnect loop.
        assert!(is_would_block(&tungstenite::Error::Io(std::io::Error::from(ErrorKind::WouldBlock))));
        assert!(is_would_block(&tungstenite::Error::Io(std::io::Error::from(ErrorKind::Interrupted))));
        assert!(!is_would_block(&tungstenite::Error::Io(std::io::Error::from(ErrorKind::ConnectionReset))));
        assert!(!is_would_block(&tungstenite::Error::ConnectionClosed));
    }
}
