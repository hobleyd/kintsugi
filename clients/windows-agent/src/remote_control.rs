//! The service's half of remote control: the sockets to the server, and the relay onto the pipe.
//!
//! # What this half is and is not
//!
//! It holds the WebSockets, because it holds this host's identity — and it does **nothing else**.
//! It never captures a screen, never posts an input event, and never decides whether a session may
//! go ahead. Those all belong to the tray process, which has the desktop; see `remote_session`.
//!
//! What it does understand is exactly one message: a granted consent, because that is the moment the
//! session socket has to be opened. Everything else is copied between the socket and the pipe
//! without being looked at — the same property the Kintsugi server itself has with respect to the
//! media protocol, one layer further down.
//!
//! # Reachability follows the pipe, not the service
//!
//! The control socket is opened only while a console-session tray process is connected, and dropped
//! when it goes. That is not a simplification, it is the correct semantics: with nobody logged in
//! there is no screen to share and nobody to ask, so the host genuinely is not reachable for remote
//! control and the server should say so. It matches macOS, where no logged-in user means no per-user
//! process and therefore no socket.
//!
//! # One thread, everything non-blocking
//!
//! Two WebSockets and a pipe are all polled from a single thread. The alternative — a thread per
//! channel — would mean sharing a `WebSocket` between a reader and a writer, and sharing a
//! non-overlapped pipe handle between two blocking calls, which is a documented way to deadlock (see
//! `remote_ipc::PipeConnection`). A 10ms poll costs nothing next to what it avoids.

use std::io::{Read, Write};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver};
use std::sync::Arc;
use std::time::{Duration, Instant};

use anyhow::{anyhow, Context, Result};
use tungstenite::client::IntoClientRequest;
use tungstenite::stream::MaybeTlsStream;
use tungstenite::{Connector, Message, WebSocket};

use crate::config::{self, Config};
use crate::identity::{self, AgentIdentity};
use crate::logging;
use crate::remote_ipc::{self, FrameReader, IpcFrame, IpcMessage, PipeConnection, PipeListener};
use crate::remote_protocol::{
    decode_shell_input, encode_shell_output, parse_server_message, parse_viewer_input, AgentMessage,
    ConsentOutcome, ServerMessage, SessionKind, ShellInfo, ViewerInput,
};
use crate::pty::{self, ProgramSpec, Pty, INITIAL_COLS, INITIAL_ROWS};
use crate::session_launcher::{console_session_with_user, SessionHelper};

/// How much terminal output is carried in one shell frame. Same value and reasoning as the other
/// two agents': a burst of output arrives as a handful of frames rather than one large one, so it
/// cannot stall the socket the keystrokes travel back on.
const SHELL_READ_BUFFER_BYTES: usize = 32 * 1024;

/// How long the relay waits between polls of its three channels.
const POLL_INTERVAL: Duration = Duration::from_millis(10);

/// How long the control socket may go without a single frame from the server before it is
/// declared dead and reopened.
///
/// The server pings every 30s (`RemoteControlController.KeepAliveInterval`), so this is three
/// missed pings. A read that returns `WouldBlock` forever looks exactly like a healthy idle
/// socket, and a socket whose network has gone away — a VPN dropping, a host waking on a different
/// network — produces nothing else: the kernel reports it `ESTABLISHED` for good, since nothing
/// will ever send a RST for a source address that no longer exists, while the server has long
/// since timed it out and marked the host unreachable. Same value and reasoning as the macOS
/// agent, where it was found. Keep it comfortably above the server's ping interval.
const CONTROL_SILENCE_TIMEOUT: Duration = Duration::from_secs(90);

const CONNECT_TIMEOUT: Duration = Duration::from_secs(15);

/// How long the TLS and WebSocket handshake may take once the TCP connection is up.
///
/// [`CONNECT_TIMEOUT`] bounds the connect and, until this existed, nothing bounded what came after
/// it: the stream is still in blocking mode through `client_tls_with_config`, so a server that
/// accepts the connection and then answers nothing parks this thread in `read` with no deadline of
/// any kind. Nothing upstream rescues it either — nginx gives `location = /api/remote-control` a
/// `proxy_read_timeout` of an hour, deliberately, because a live session is legitimately silent for
/// minutes at a time, and that timeout applies just as happily to a handshake that never finishes.
/// Cleared the moment the handshake is done, since `set_nonblocking` governs every read after it.
const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(20);

/// Reconnect backoff for the control socket. As the macOS agent: a host that cannot reach the server
/// is a host nobody can connect to anyway, so there is nothing to gain from hammering it.
const INITIAL_RECONNECT_BACKOFF: Duration = Duration::from_secs(5);
const MAX_RECONNECT_BACKOFF: Duration = Duration::from_secs(60);

/// How long to keep trying to flush a granted consent before opening the session socket anyway.
///
/// The same race the macOS agent has, closed the same way but far more cheaply: there the answer
/// crosses a thread boundary and needs an acknowledgement, whereas here the thread that writes the
/// consent is the thread that opens the socket, so it can simply flush first. The relay refuses a
/// session socket for a grant it has not seen — and refuses it *after* accepting the WebSocket
/// upgrade, so the failure would arrive as a healthy connection that immediately closes, with
/// nothing to retry against.
const CONSENT_FLUSH_TIMEOUT: Duration = Duration::from_secs(5);

/// How long one attempt at the *session* socket's TCP connect may take, and how many are made.
///
/// Deliberately shorter than [`CONNECT_TIMEOUT`], and the difference is the whole point. The
/// control socket can afford to wait, because a failure there costs a log line and a backoff before
/// it tries again — which is exactly what made one fleet's flaky link invisible for months: a host
/// whose SYNs to the server are intermittently blackholed reconnects its control socket a few
/// seconds later and looks perfectly healthy, while a session socket, having had one attempt,
/// reports "could not connect" and loses the session. A blackholed SYN is not answered by waiting
/// longer, it is answered by a fresh connection, so this budget buys attempts rather than patience.
const SESSION_CONNECT_TIMEOUT: Duration = Duration::from_secs(7);
const SESSION_CONNECT_ATTEMPTS: u32 = 3;
const SESSION_CONNECT_RETRY_DELAY: Duration = Duration::from_millis(300);

/// The server's own deadline for pairing the two sockets of a session
/// (`RemoteControlSessionBroker.RemoteControlPairingTimeout`).
///
/// Never consulted at runtime — it is here so the test below can hold every wait this agent makes
/// before its socket arrives inside it. An agent still retrying when the server gives up reports
/// nothing at all, and the administrator is told "the other end never connected", which names
/// neither the host nor the reason.
#[cfg(test)]
const SERVER_PAIRING_TIMEOUT: Duration = Duration::from_secs(30);

/// How often the service asks Windows whether there is still a console session with a user in it.
///
/// Also how long a freshly logged-in host waits before becoming reachable. Two seconds is
/// imperceptible next to the time it takes somebody to sign in and open a browser, and it is two
/// syscalls.
const SESSION_POLL_INTERVAL: Duration = Duration::from_secs(2);

/// How long to wait for a launched helper to connect to the pipe.
///
/// Generous: the helper's only work before connecting is process startup, but that is on a host
/// which may be busy installing something.
const HELPER_CONNECT_TIMEOUT: Duration = Duration::from_secs(15);

/// How long the accept thread waits after a failed accept before trying again, so a persistent
/// failure logs at a readable rate rather than spinning.
const ACCEPT_RETRY_INTERVAL: Duration = Duration::from_secs(5);

type Socket = WebSocket<MaybeTlsStream<std::net::TcpStream>>;

/// Runs for the life of the service. Never returns until `shutdown` is set.
///
/// # Reachability no longer follows the console session
///
/// It followed the tray process holding the pipe, then — once capture moved into a helper that
/// exists only while a session runs — [`console_session_with_user`], so the socket was held open
/// exactly while somebody was logged in. That is the right test for a *screen* session and quite
/// wrong for a shell: a terminal needs no desktop, and a server with nobody signed in is precisely
/// the host an administrator wants one on. So the socket is now held whenever this host has an
/// identity, the console session is tracked as a *capability* rather than a precondition, and a
/// screen request arriving with nobody logged in is answered `Unavailable` — the agent saying
/// "there is nobody here" at once, rather than the host being invisible and the administrator being
/// told the agent is unreachable.
pub fn run(config: Config, serial_number: String, shutdown: Arc<AtomicBool>) {
    let listener = match PipeListener::create() {
        Ok(listener) => Arc::new(listener),
        Err(err) => {
            // Not fatal to the service: everything else it does still works, and remote control is
            // the only thing lost. Reported loudly because nothing else will explain why the Hosts
            // screen reports this host as unreachable.
            logging::error(&format!("remote control is unavailable on this host: {err:#}"));
            return;
        }
    };

    // One accept thread for the service's whole life, rather than an accept per session.
    //
    // `ConnectNamedPipe` blocks with no timeout, so calling it from the relay loop would stall the
    // control socket whenever a helper failed to start. Blocking forever is fine on a thread whose
    // only job is to block: it sits in the accept between sessions, hands over the connection when
    // a helper appears, and goes back to waiting.
    let (connection_tx, connection_rx) = mpsc::channel::<(PipeConnection, u32)>();
    {
        let listener = listener.clone();
        std::thread::spawn(move || loop {
            match listener.accept() {
                Ok(accepted) => {
                    if connection_tx.send(accepted).is_err() {
                        return;
                    }
                }
                Err(err) => {
                    logging::warn(&format!("could not accept a remote control client: {err:#}"));
                    std::thread::sleep(ACCEPT_RETRY_INTERVAL);
                }
            }
        });
    }

    let mut backoff = INITIAL_RECONNECT_BACKOFF;

    while !shutdown.load(Ordering::SeqCst) {
        // Re-read from disk each time rather than taking an identity once. The service starts before
        // enrollment has necessarily happened, and it outlives a re-enrollment.
        //
        // `load` returns a Result here, unlike the macOS agent's: on Windows an identity that
        // cannot be *read* is told apart from one that does not exist, because the two need
        // completely different fixes and conflating them once had this agent re-enrolling on every
        // check-in forever.
        let identity = match identity::load(&config::identity_dir()) {
            Ok(Some(identity)) => identity,
            Ok(None) => {
                logging::warn("remote control is unavailable until this host has enrolled an identity");
                std::thread::sleep(backoff);
                backoff = (backoff * 2).min(MAX_RECONNECT_BACKOFF);
                continue;
            }
            Err(err) => {
                logging::error(&format!(
                    "remote control is unavailable: this host's identity exists but could not be read: {err:#}"
                ));
                std::thread::sleep(backoff);
                backoff = (backoff * 2).min(MAX_RECONNECT_BACKOFF);
                continue;
            }
        };

        match relay(&config, &serial_number, &identity, &connection_rx, &shutdown) {
            Ok(()) => {
                logging::info("the remote control socket closed; reconnecting");
                backoff = INITIAL_RECONNECT_BACKOFF;
            }
            Err(err) => {
                // {err:#} for the cause chain, the same reason post_with_retry does it: every
                // connection failure has an identical outermost message and the interesting part
                // ("invalid peer certificate: UnknownIssuer") is underneath.
                logging::warn(&format!("the remote control relay stopped: {err:#}"));
                std::thread::sleep(backoff);
                backoff = (backoff * 2).min(MAX_RECONNECT_BACKOFF);
            }
        }
    }
}

/// Everything in flight for one control socket.
struct Relay {
    control: Socket,
    session: Option<Socket>,
    session_id: Option<String>,
    reader: FrameReader,
    /// The pipe to the session helper, and the helper itself. Both exist only while a session is
    /// running, and they are torn down together — a helper without a pipe is capturing to nowhere,
    /// and a pipe without a helper is a dead file handle.
    pipe: Option<PipeConnection>,
    helper: Option<SessionHelper>,
    /// The terminal, on a shell session and only then. Its presence is what tells the loop which
    /// kind of session is running: a screen session's bytes go to and from the helper, a shell
    /// session's to and from this — no helper, no pipe and no desktop involved.
    terminal: Option<Pty>,
    /// Why a shell session finished, when [`pump_shell_session`] is the thing that found out.
    shell_end_reason: Option<String>,
}

impl Relay {
    /// Stops the helper and drops its pipe.
    ///
    /// Dropping the pipe is what releases the listener's instance for the next session — see
    /// `PipeConnection`'s `Drop`, which disconnects without closing.
    fn end_session(&mut self) {
        self.session = None;
        self.session_id = None;
        self.pipe = None;

        if let Some(helper) = self.helper.take() {
            helper.stop();
        }

        // A shell that outlived its session would go on holding a console host with it, in a
        // service that runs for months.
        if let Some(terminal) = self.terminal.take() {
            if let Err(err) = terminal.terminate() {
                logging::warn(&format!("could not end the terminal: {err}"));
            }
        }
    }
}

impl Drop for Relay {
    /// The backstop for every path out of [`relay`] that is not a session ending — which is to say
    /// the common one: the control socket is reset, `relay` returns `Err`, and the reconnect loop
    /// builds a fresh `Relay`. Without this the helper that was capturing is simply abandoned.
    ///
    /// A leaked helper is not merely a stray process. It reconnects to the service's pipe, its
    /// connection sits in the accept channel, and the *next* session drains it, sees a pid that is
    /// not the helper it launched, and discards it — which used to disconnect the new helper along
    /// with it (see `remote_ipc::PipeListener::accept`). One dropped control socket therefore cost
    /// the following screen session too, and nothing in the log connected the two.
    fn drop(&mut self) {
        self.end_session();
    }
}

fn relay(
    config: &Config,
    serial_number: &str,
    identity: &AgentIdentity,
    connections: &Receiver<(PipeConnection, u32)>,
    shutdown: &Arc<AtomicBool>,
) -> Result<()> {
    let url = config.remote_control_url(serial_number, None);
    let mut control = connect(&url, identity, CONNECT_TIMEOUT)?;
    set_nonblocking(&control)?;

    logging::info(&format!("remote control socket open to {url}"));

    queue(
        &mut control,
        &AgentMessage::Hello {
            agent_version: env!("CARGO_PKG_VERSION").to_string(),
            console_user: None,
        },
    )?;

    let mut relay = Relay {
        control,
        session: None,
        session_id: None,
        reader: FrameReader::new(),
        pipe: None,
        helper: None,
        terminal: None,
        shell_end_reason: None,
    };

    // Which console session has somebody logged into it, if any. `None` is an ordinary state here
    // rather than a fault — it only means screen sessions cannot be offered.
    let mut console_session = console_session_with_user();
    let mut next_session_check = Instant::now() + SESSION_POLL_INTERVAL;

    // The last time anything at all arrived on the control socket — a message or the server's
    // keep-alive ping. See `CONTROL_SILENCE_TIMEOUT`.
    let mut last_heard = Instant::now();

    // Whether the session socket still holds bytes the network has not taken. See step 2.
    let mut session_backlogged = false;

    while !shutdown.load(Ordering::SeqCst) {
        // 0. Has the user logged out from under us? Checked on a timer rather than every pass —
        //    it is two syscalls and it cannot change between frames in any way that matters.
        if Instant::now() >= next_session_check {
            next_session_check = Instant::now() + SESSION_POLL_INTERVAL;
            let current = console_session_with_user();
            if current != console_session {
                console_session = current;

                // A screen session cannot survive the desktop it was capturing. A shell session is
                // untouched — it never had one — and neither is the control socket, which used to
                // come down here and took this host's reachability with it.
                if relay.helper.is_some() {
                    logging::info("the console session ended; stopping the remote control session");
                    let session_id = relay.session_id.clone().unwrap_or_default();
                    relay.end_session();
                    queue(
                        &mut relay.control,
                        &AgentMessage::SessionEnded {
                            session_id,
                            reason: "the host's desktop session ended".to_string(),
                        },
                    )?;
                }
            }
        }

        // 1. The server, to the helper.
        match read_text(&mut relay.control)? {
            SocketRead::Message(text) => {
                last_heard = Instant::now();
                if let Some(line) =
                    handle_server_message(&text, console_session, config, serial_number, identity, connections, &mut relay)?
                {
                    logging::info(&format!("remote control: {line}"));
                }
            }
            SocketRead::KeepAlive => last_heard = Instant::now(),
            SocketRead::Closed => {
                relay.end_session();
                return Ok(());
            }
            SocketRead::Idle => {
                if last_heard.elapsed() > CONTROL_SILENCE_TIMEOUT {
                    relay.end_session();
                    return Err(anyhow!(
                        "nothing heard from the server for {}s; treating the control socket as dead",
                        last_heard.elapsed().as_secs()
                    ));
                }
            }
        }

        // 2. The helper, to the server or to the viewer.
        //
        // Not drained while the session socket is backlogged. Tiles are produced at the capture rate
        // and leave at the network's, and when the second is slower nothing here may simply keep
        // queueing — tungstenite's out-buffer is unbounded, so that is a growing delay and a growing
        // heap. The macOS agent captures in-process and pauses capture instead; here the capture is
        // in the helper, so the relay pauses *reading* and lets the pipe fill: the helper's blocking
        // `write_all` stalls until this end reads again, which paces its capture loop to the network
        // without a message in either direction. Step 3 still runs, so a viewer that hangs up
        // mid-backlog is noticed and the backlog discarded with the socket.
        if relay.pipe.is_some() && !session_backlogged {
            let chunk = match relay.pipe.as_mut().expect("checked").read_available() {
                Ok(chunk) => chunk,
                Err(err) => {
                    // The helper has gone — it exited, or was killed. That ends the session rather
                    // than the socket: the host is still reachable and can be asked again.
                    logging::info(&format!("the session helper disconnected: {err:#}"));
                    let session_id = relay.session_id.clone().unwrap_or_default();
                    relay.end_session();
                    queue(
                        &mut relay.control,
                        &AgentMessage::SessionEnded {
                            session_id,
                            reason: "the host's session helper stopped".to_string(),
                        },
                    )?;
                    Vec::new()
                }
            };

            if !chunk.is_empty() {
                relay.reader.push(&chunk);
            }

            while let Some(frame) = relay.reader.next_frame()? {
                handle_helper_frame(frame, config, serial_number, identity, &mut relay)?;
            }
        }

        // 3. The viewer's input: into the terminal on a shell session, on to the helper on a
        //    screen one.
        if relay.terminal.is_some() {
            if !pump_shell_session(&mut relay)? {
                let session_id = relay.session_id.clone().unwrap_or_default();
                let reason = relay.shell_end_reason.take().unwrap_or_else(|| "the viewer disconnected".to_string());
                logging::info(&format!("remote shell session {session_id} ended: {reason}"));
                relay.end_session();
                queue(&mut relay.control, &AgentMessage::SessionEnded { session_id, reason })?;
            }
        } else if let Some(session) = relay.session.as_mut() {
            match read_text(session)? {
                SocketRead::Message(json) => {
                    if let Some(pipe) = relay.pipe.as_mut() {
                        write_pipe(pipe, &IpcMessage::ViewerInput { json })?;
                    }
                }
                SocketRead::Closed => {
                    logging::info("the viewer disconnected");
                    let session_id = relay.session_id.clone().unwrap_or_default();
                    relay.end_session();
                    queue(
                        &mut relay.control,
                        &AgentMessage::SessionEnded {
                            session_id,
                            reason: "the viewer disconnected".to_string(),
                        },
                    )?;
                }
                // A session socket carries frames outbound, so a dead one fails on the write and
                // needs no silence watchdog of its own.
                SocketRead::KeepAlive | SocketRead::Idle => {}
            }
        }

        // 4. Push whatever is queued on either socket. Tolerates a partial flush: tungstenite keeps
        //    the remainder and the next pass sends it.
        flush(&mut relay.control)?;
        session_backlogged = match relay.session.as_mut() {
            Some(session) => !flush(session)?,
            None => false,
        };

        std::thread::sleep(POLL_INTERVAL);
    }

    relay.end_session();
    Ok(())
}

/// Starts a shell and joins it to a session socket.
///
/// # Why nobody is asked
///
/// This is the one place remote control's consent rule does not apply, and it is deliberate. A shell
/// session is the access an administrator of this fleet already has by other means — it is what they
/// would reach a remote PowerShell session for — and the only reason it is routed through this agent
/// is that WinRM or SSH would need an inbound port on every managed machine, which is exactly what
/// the relay design exists to avoid. The compensating control is the server's: the same
/// `remote_control_sessions` row a screen request writes is written here, naming who asked and when.
///
/// # Why it is not in the session helper
///
/// Everything the helper exists for is about a *desktop* — answering a consent dialog, capturing a
/// screen, posting input, following Windows onto the secure desktop. A terminal needs none of it, so
/// launching a helper would add a process, a named pipe and a logged-in user as requirements for
/// something that has no use for any of them — and the logged-in user is the requirement that would
/// hurt, because a server with nobody signed in is the host most likely to want a shell.
///
/// So the terminal runs here, in the service, as **SYSTEM**. That is the strongest of the three
/// agents' shells (root on both macOS and Linux), and it is stated on the wire rather than left to
/// be remembered: [`ShellInfo`] carries the account so the viewer can say which it is.
fn start_shell_session(
    config: &Config,
    serial_number: &str,
    identity: &AgentIdentity,
    relay: &mut Relay,
    session_id: &str,
    requested_by: &str,
) -> Result<()> {
    // Everything that can fail locally is done before the answer is sent, so a host that cannot
    // produce a terminal reports `Unavailable` rather than accepting a session and then failing.
    let started = shell_program().and_then(|(program, user)| {
        // Logged before the spawn rather than after a successful connect, because a session that
        // fails between the two used to name no program at all — leaving no way to tell from the
        // log which of pwsh, powershell and cmd this host chose.
        logging::info(&format!("starting a remote shell: {} as {user}", program.program.display()));

        let terminal = Pty::spawn(&program, INITIAL_COLS, INITIAL_ROWS)
            .with_context(|| format!("could not start {} in a terminal", program.program.display()))?;
        Ok((program, user, terminal))
    });

    let (program, user, terminal) = match started {
        Ok(started) => started,
        Err(err) => {
            logging::warn(&format!("cannot open a shell session: {err:#}"));
            return queue(
                &mut relay.control,
                &AgentMessage::Consent {
                    session_id: session_id.to_string(),
                    outcome: ConsentOutcome::Unavailable,
                },
            );
        }
    };

    queue(
        &mut relay.control,
        &AgentMessage::Consent {
            session_id: session_id.to_string(),
            outcome: ConsentOutcome::NotRequired,
        },
    )?;

    // Flushed before the session socket is opened, the same race the granted path waits out and for
    // the same reason: the relay refuses a session socket for an answer it has not seen, and refuses
    // it *after* accepting the upgrade. See CONSENT_FLUSH_TIMEOUT.
    let deadline = Instant::now() + CONSENT_FLUSH_TIMEOUT;
    while Instant::now() < deadline {
        if flush(&mut relay.control)? {
            break;
        }
        std::thread::sleep(POLL_INTERVAL);
    }

    match connect_session_socket(config, serial_number, identity, session_id) {
        Ok(mut session) => {
            set_nonblocking(&session)?;

            // Before the first output frame, so the viewer can say what it is attached to — the
            // shell analogue of the display geometry a screen session sends first.
            let info = serde_json::to_string(&ShellInfo::new(program.program.display().to_string(), &user))
                .context("could not describe the shell")?;
            write_queued(&mut session, Message::text(info))?;

            relay.session = Some(session);
            relay.session_id = Some(session_id.to_string());
            relay.terminal = Some(terminal);
            relay.shell_end_reason = None;

            logging::info(&format!(
                "remote shell session {session_id} started for {requested_by} ({} as {user})",
                program.program.display()
            ));
            Ok(())
        }
        Err(err) => {
            // The terminal is running by now and has to be hung up on, or a shell is left attached
            // to a session that never happened.
            logging::warn(&format!("could not open the remote shell session socket: {err:#}"));
            let _ = terminal.terminate();
            queue(
                &mut relay.control,
                &AgentMessage::SessionEnded {
                    session_id: session_id.to_string(),
                    reason: format!("the host could not open its session socket: {err:#}"),
                },
            )
        }
    }
}

/// Moves one pass of bytes between the terminal and the viewer. `Ok(false)` means the session is
/// over, and [`Relay::shell_end_reason`] says why unless the viewer simply hung up.
///
/// Identical in shape to the Linux agent's function of the same name — a terminal is small enough
/// that one function is both halves, unlike a tile stream, which needs the backpressure step 2
/// performs.
fn pump_shell_session(relay: &mut Relay) -> Result<bool> {
    let Some(session) = relay.session.as_mut() else {
        return Ok(false);
    };
    let Some(terminal) = relay.terminal.as_mut() else {
        return Ok(false);
    };

    // The viewer, into the terminal. This is the one place in this protocol where a *binary*
    // message travels from the browser to the agent: keystrokes are raw bytes rather than JSON,
    // because a terminal carries arbitrary bytes and escaping each one would double the traffic on
    // the half of the connection latency is measured on.
    loop {
        match session.read() {
            Ok(Message::Binary(bytes)) => {
                // Anything that is not shell input is dropped rather than typed.
                if let Some(input) = decode_shell_input(&bytes) {
                    if let Err(err) = terminal.write_all(input) {
                        relay.shell_end_reason = Some(format!("writing to the terminal failed: {err}"));
                        return Ok(false);
                    }
                }
            }
            Ok(Message::Text(text)) => {
                if let Some(ViewerInput::Resize { cols, rows }) = parse_viewer_input(&text) {
                    // Not worth ending a session over: the shell keeps working, it just wraps at
                    // the wrong width.
                    if let Err(err) = terminal.resize(cols, rows) {
                        logging::warn(&format!("could not resize the terminal: {err}"));
                    }
                }
            }
            Ok(Message::Close(_)) => return Ok(false),
            Ok(_) => {}
            Err(err) if is_would_block(&err) => break,
            Err(tungstenite::Error::ConnectionClosed) | Err(tungstenite::Error::AlreadyClosed) => return Ok(false),
            Err(err) => {
                relay.shell_end_reason = Some(format!("reading from the session socket failed: {err}"));
                return Ok(false);
            }
        }
    }

    // The terminal, out to the viewer. Read once per pass rather than drained: a long-running
    // command producing output continuously would otherwise never let the input half above run
    // again, and the administrator could not press Ctrl-C.
    let mut buffer = [0u8; SHELL_READ_BUFFER_BYTES];
    match terminal.read_available(&mut buffer) {
        // The console host has closed its end — the shell exited. The ordinary way a session
        // finishes, and not a fault.
        Ok(Some(0)) => {
            relay.shell_end_reason = Some("the shell exited".to_string());
            return Ok(false);
        }
        Ok(Some(read)) => write_queued(session, Message::Binary(encode_shell_output(&buffer[..read]).into()))?,
        Ok(None) => {
            // Checked only once the terminal has nothing left to say, so its last output is on its
            // way first. Needed as well as the broken pipe above: a shell that exits while another
            // process still holds the console open produces no broken pipe at all.
            match terminal.try_wait() {
                Ok(Some(_)) => {
                    relay.shell_end_reason = Some("the shell exited".to_string());
                    return Ok(false);
                }
                Ok(None) => {}
                Err(err) => {
                    relay.shell_end_reason = Some(format!("could not check on the shell: {err}"));
                    return Ok(false);
                }
            }
        }
        Err(err) => {
            relay.shell_end_reason = Some(format!("reading from the terminal failed: {err}"));
            return Ok(false);
        }
    }

    Ok(true)
}

/// Windows PowerShell, or PowerShell 7 where it is installed, and the account it will run as.
///
/// PowerShell rather than `cmd.exe` because it is what every script this agent runs is written in
/// (see `ScriptLanguages.For` on the server) — an administrator opening a terminal on a Windows host
/// is there to run the same kind of thing by hand. PowerShell 7 is preferred where present for its
/// far better VT handling, which is what the viewer is reading.
///
/// The account is not looked up: this is the service, and `install.ps1` pins `obj= LocalSystem` on
/// both its branches, which is load-bearing for two other reasons already (reading the identity
/// directory, and `SE_TCB_NAME` for the session helper). Naming it here rather than querying the
/// token keeps this honest if that ever changes — a wrong label in the viewer would be worse than
/// none, and `sc.exe qc KintsugiAgent` is the thing to check.
fn shell_program() -> Result<(ProgramSpec, String)> {
    let system_root = std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".to_string());
    let program_files = std::env::var("ProgramFiles").unwrap_or_else(|_| r"C:\Program Files".to_string());

    let candidates = [
        format!(r"{program_files}\PowerShell\7\pwsh.exe"),
        format!(r"{system_root}\System32\WindowsPowerShell\v1.0\powershell.exe"),
        format!(r"{system_root}\System32\cmd.exe"),
    ];
    let borrowed: Vec<&str> = candidates.iter().map(String::as_str).collect();

    let program = pty::first_existing(&borrowed)
        .ok_or_else(|| anyhow!("no usable shell was found on this host (looked for {})", candidates.join(", ")))?;

    // `-NoLogo` because the banner is noise in a support session, and no `-NoProfile`: an
    // administrator's profile is most of what makes their shell useful, exactly as the `-l` the two
    // Unix agents pass gets them theirs.
    let args = if program.to_string_lossy().to_lowercase().ends_with("cmd.exe") {
        Vec::new()
    } else {
        vec!["-NoLogo".to_string()]
    };

    Ok((
        ProgramSpec {
            program,
            args,
            env: vec![
                ("SystemRoot".to_string(), system_root.clone()),
                ("SystemDrive".to_string(), std::env::var("SystemDrive").unwrap_or_else(|_| "C:".to_string())),
                ("ProgramFiles".to_string(), program_files),
                // Enough for the shell to find its own modules and the usual tooling; anything
                // beyond it is the profile's business.
                (
                    "PATH".to_string(),
                    format!(r"{system_root}\System32;{system_root};{system_root}\System32\WindowsPowerShell\v1.0"),
                ),
            ],
            cwd: std::path::PathBuf::from(&system_root),
        },
        "SYSTEM".to_string(),
    ))
}

/// Launches the session helper and waits for it to connect.
///
/// The process id is checked against the one just launched. That is a stronger guarantee than the
/// old console-session test and it composes with the pipe's ACL, which admits nothing below SYSTEM
/// or Administrators — so an unexpected process cannot be on the other end, and if somehow one is,
/// it is refused rather than handed a session.
fn start_session_helper(
    console_session: u32,
    connections: &Receiver<(PipeConnection, u32)>,
) -> Result<(PipeConnection, SessionHelper)> {
    // Anything already waiting belongs to a session that is over: a helper this service lost track
    // of reconnects to the pipe, and its connection sits in the channel until somebody drains it.
    // Drained *before* the new helper is launched, so that discarding one can never disconnect the
    // helper that is about to arrive — which is precisely what it used to do.
    while let Ok((pipe, client_pid)) = connections.try_recv() {
        logging::warn(&format!("discarding a stale remote control connection from pid {client_pid}"));
        drop(pipe);
    }

    let helper = SessionHelper::launch(console_session)?;

    loop {
        let (pipe, client_pid) = connections
            .recv_timeout(HELPER_CONNECT_TIMEOUT)
            .map_err(|_| anyhow!("the session helper did not connect within {HELPER_CONNECT_TIMEOUT:?}"))?;

        if client_pid == helper.pid() {
            return Ok((pipe, helper));
        }

        // A connection from something that is not the helper we launched. Dropped, and the wait
        // resumes rather than failing: the pipe's ACL means this can only be another SYSTEM or
        // Administrator process, most plausibly a helper left over from a session that has just
        // ended, and treating a stale one as fatal would lose the session that is starting.
        logging::warn(&format!(
            "ignoring a remote control connection from pid {client_pid}; expecting the helper at pid {}",
            helper.pid()
        ));
        drop(pipe);
    }
}

/// Forwards one server message onward, launching the helper if this is a new request.
#[allow(clippy::too_many_arguments)]
fn handle_server_message(
    text: &str,
    console_session: Option<u32>,
    config: &Config,
    serial_number: &str,
    identity: &AgentIdentity,
    connections: &Receiver<(PipeConnection, u32)>,
    relay: &mut Relay,
) -> Result<Option<String>> {
    let message = match parse_server_message(text) {
        Ok(Some(message)) => message,
        // A newer server mentioning something this build has never heard of. Logged and ignored,
        // exactly as the server treats an unrecognised message from an agent.
        Ok(None) => return Ok(Some("ignoring an unrecognised message from the server".to_string())),
        Err(err) => return Ok(Some(format!("could not read a message from the server: {err}"))),
    };

    match message {
        ServerMessage::SessionRequested { session_id, kind, requested_by, consent_timeout_seconds } => {
            if relay.helper.is_some() || relay.terminal.is_some() {
                // One session per host, which the server also enforces. Refusing is the answer that
                // cannot go wrong.
                queue(
                    &mut relay.control,
                    &AgentMessage::Consent { session_id, outcome: ConsentOutcome::Denied },
                )?;
                return Ok(Some("refused a second session on a host already in one".to_string()));
            }

            // A kind this build has never heard of. Answered rather than ignored, so the
            // administrator is told now instead of waiting out a consent timeout.
            if kind == SessionKind::Unknown {
                queue(
                    &mut relay.control,
                    &AgentMessage::Consent { session_id: session_id.clone(), outcome: ConsentOutcome::Unavailable },
                )?;
                return Ok(Some(format!(
                    "refusing session {session_id}: this agent does not implement that kind of session"
                )));
            }

            // A terminal needs no desktop and no consent, and it runs as the account the service
            // already is. So it is started here rather than in a session helper — see
            // `start_shell_session`.
            if kind == SessionKind::Shell {
                let line = format!("shell session {session_id} requested by {requested_by}");
                start_shell_session(config, serial_number, identity, relay, &session_id, &requested_by)?;
                return Ok(Some(line));
            }

            // Nobody is logged in, so there is no screen to share and nobody to ask.
            let Some(console_session) = console_session else {
                queue(
                    &mut relay.control,
                    &AgentMessage::Consent { session_id: session_id.clone(), outcome: ConsentOutcome::Unavailable },
                )?;
                return Ok(Some(format!(
                    "refusing screen session {session_id}: nobody is logged in at this host"
                )));
            };

            let line = format!("session {session_id} requested by {requested_by}");

            match start_session_helper(console_session, connections) {
                Ok((mut pipe, helper)) => {
                    let handed_over = write_pipe(
                        &mut pipe,
                        &IpcMessage::SessionRequested {
                            session_id: session_id.clone(),
                            requested_by: requested_by.clone(),
                            consent_timeout_seconds,
                        },
                    );

                    match handed_over {
                        Ok(()) => {
                            relay.pipe = Some(pipe);
                            relay.helper = Some(helper);
                        }

                        // The helper is running and has been told nothing, so it is stopped here.
                        // It used to be dropped instead — `?` propagated, and neither the pipe nor
                        // the process was in the `Relay` yet, so `Drop` could not reach it either —
                        // which left exactly the orphan that goes on to break the *next* session.
                        // Reported as a refusal for the same reason the arm below is, and
                        // deliberately not propagated: one session's failed hand-off is no reason
                        // to drop the control socket and make the whole host briefly unreachable.
                        Err(err) => {
                            drop(pipe);
                            helper.stop();
                            logging::error(&format!("could not hand the session to the helper: {err:#}"));
                            queue(
                                &mut relay.control,
                                &AgentMessage::Consent { session_id, outcome: ConsentOutcome::Denied },
                            )?;
                            return Ok(Some(format!("{line}, but the session could not be handed over: {err:#}")));
                        }
                    }
                }
                Err(err) => {
                    // Nothing was captured and nobody was asked, so this is reported as a refusal
                    // rather than left to time out — the administrator gets an answer and the audit
                    // record says the host could not ask.
                    logging::error(&format!("could not start a remote control session: {err:#}"));
                    queue(
                        &mut relay.control,
                        &AgentMessage::Consent { session_id, outcome: ConsentOutcome::Denied },
                    )?;
                    return Ok(Some(format!("{line}, but the session helper would not start: {err:#}")));
                }
            }

            Ok(Some(line))
        }

        ServerMessage::SessionEnded { session_id, reason } => {
            // The helper is stopped rather than asked to stop: it holds no lock and is mid-way
            // through nothing but a screen capture, and killing it is what guarantees the capture
            // has actually ceased.
            relay.end_session();
            Ok(Some(format!("the server ended session {session_id}: {reason}")))
        }
    }
}

/// Acts on one message from the session helper.
fn handle_helper_frame(
    frame: IpcFrame,
    config: &Config,
    serial_number: &str,
    identity: &AgentIdentity,
    relay: &mut Relay,
) -> Result<()> {
    match frame {
        IpcFrame::Json(IpcMessage::Consent { session_id, outcome }) => {
            logging::info(&format!("the console user answered session {session_id}: {outcome:?}"));

            queue(
                &mut relay.control,
                &AgentMessage::Consent { session_id: session_id.clone(), outcome },
            )?;

            if outcome != ConsentOutcome::Granted {
                return Ok(());
            }

            // Flushed before the session socket is opened — see CONSENT_FLUSH_TIMEOUT.
            let deadline = Instant::now() + CONSENT_FLUSH_TIMEOUT;
            while Instant::now() < deadline {
                if flush(&mut relay.control)? {
                    break;
                }
                std::thread::sleep(POLL_INTERVAL);
            }

            match connect_session_socket(config, serial_number, identity, &session_id) {
                Ok(session) => {
                    set_nonblocking(&session)?;
                    relay.session = Some(session);
                    relay.session_id = Some(session_id);
                }
                Err(err) => {
                    // The tray is already capturing at this point, so it has to be told to stop —
                    // otherwise it streams frames into a pipe whose other end has nowhere to put
                    // them.
                    logging::warn(&format!("could not open the remote control session socket: {err:#}"));
                    queue(
                        &mut relay.control,
                        &AgentMessage::SessionEnded {
                            session_id,
                            reason: format!("the host could not open its session socket: {err:#}"),
                        },
                    )?;
                }
            }

            Ok(())
        }

        IpcFrame::Json(IpcMessage::DisplayInfo { json }) => {
            if let Some(session) = relay.session.as_mut() {
                write_queued(session, Message::text(json))?;
            }
            Ok(())
        }

        IpcFrame::Json(IpcMessage::EndedByHost { session_id, reason }) => {
            logging::info(&format!("the console user ended session {session_id}: {reason}"));
            relay.session = None;
            relay.session_id = None;
            queue(&mut relay.control, &AgentMessage::SessionEnded { session_id, reason })
        }

        IpcFrame::Tile(tile) => {
            // Relayed as a binary message and never inspected: what a tile means is a contract
            // between the tray process and the browser.
            if let Some(session) = relay.session.as_mut() {
                write_queued(session, Message::Binary(tile.into()))?;
            }
            Ok(())
        }

        // The three service-to-helper variants, which the helper never sends back. Ignored rather
        // than fatal, so a version skew between the two halves cannot take the relay down.
        IpcFrame::Json(other) => {
            logging::warn(&format!("ignoring an unexpected message from the session helper: {other:?}"));
            Ok(())
        }
    }
}

enum SocketRead {
    Message(String),
    /// A ping, a pong or anything else that is not a message but proves the peer is still there.
    KeepAlive,
    Closed,
    Idle,
}

fn read_text(socket: &mut Socket) -> Result<SocketRead> {
    match socket.read() {
        Ok(Message::Text(text)) => Ok(SocketRead::Message(text.to_string())),
        Ok(Message::Close(_)) => Ok(SocketRead::Closed),
        // Ping and pong are answered inside tungstenite but still handed back here, which is what the
        // control socket's silence watchdog listens for. A binary message on either of these sockets
        // is not part of the protocol in this direction and is ignored — but it still counts as the
        // peer talking.
        Ok(_) => Ok(SocketRead::KeepAlive),
        Err(err) if is_would_block(&err) => Ok(SocketRead::Idle),
        Err(tungstenite::Error::ConnectionClosed) | Err(tungstenite::Error::AlreadyClosed) => Ok(SocketRead::Closed),
        Err(err) => Err(anyhow!(err).context("reading from a remote control socket")),
    }
}

fn write_pipe(pipe: &mut PipeConnection, message: &IpcMessage) -> Result<()> {
    pipe.write_all(&remote_ipc::encode_json(message)?)
}

/// Queues a message on a socket. Does not flush — a flush that cannot complete right now on a
/// non-blocking socket is ordinary, not a failure.
fn queue(socket: &mut Socket, message: &AgentMessage) -> Result<()> {
    let json = serde_json::to_string(message).context("could not serialise a remote control message")?;
    write_queued(socket, Message::text(json))
}

/// Queues one message on a non-blocking socket without requiring that it leave right now.
///
/// `WebSocket::write` is not the pure "queue it" it reads as. tungstenite formats the frame into its
/// own out-buffer and then, once that buffer holds more than its 128 KiB write threshold, tries the
/// socket — and with rustls in front, *every* write first tries to push the TLS bytes a previous
/// write left pending (`rustls::Stream::complete_prior_io`). On a non-blocking socket either attempt
/// reports `WouldBlock` the moment the kernel's send buffer is full, which a burst of JPEG tiles
/// reaches on the first full frame. The frame is already in the out-buffer by then — nothing is
/// lost, and the loop's `flush` step sends it once the network catches up — so `WouldBlock` here is
/// the same non-event it is in [`flush`]. Treated as fatal it ended the Linux agent's every Wayland
/// session about one second after consent, as "the remote control relay stopped: IO error: Resource
/// temporarily unavailable (os error 11)" — the same code as here.
///
/// Generic over the stream so a test can stand in a socket that refuses every write.
fn write_queued<S: Read + Write>(socket: &mut WebSocket<S>, message: Message) -> Result<()> {
    match socket.write(message) {
        Ok(()) => Ok(()),
        Err(err) if is_would_block(&err) => Ok(()),
        Err(err) => Err(anyhow!(err).context("could not queue a remote control message")),
    }
}

/// `Ok(false)` means the socket could not take all of it yet, which is not an error.
fn flush(socket: &mut Socket) -> Result<bool> {
    match socket.flush() {
        Ok(()) => Ok(true),
        Err(err) if is_would_block(&err) => Ok(false),
        Err(err) => Err(anyhow!(err).context("flushing a remote control socket")),
    }
}

/// Opens the session socket, retrying a connect that failed to establish.
///
/// Retried because a session socket has one chance where the control socket has unlimited ones —
/// see [`SESSION_CONNECT_ATTEMPTS`]. Deliberately *not* a retry for the consent race, which
/// [`CONSENT_FLUSH_TIMEOUT`] closes instead and which reconnecting could not fix anyway: the server
/// accepts the upgrade and only then checks the grant, so a refusal arrives as a healthy connection
/// that immediately closes.
fn connect_session_socket(
    config: &Config,
    serial_number: &str,
    identity: &AgentIdentity,
    session_id: &str,
) -> Result<Socket> {
    let url = config.remote_control_url(serial_number, Some(session_id));
    let mut last_error = None;

    for attempt in 1..=SESSION_CONNECT_ATTEMPTS {
        match connect(&url, identity, SESSION_CONNECT_TIMEOUT) {
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

/// Opens one `wss://` socket presenting this host's client certificate.
fn connect(url: &str, identity: &AgentIdentity, connect_timeout: Duration) -> Result<Socket> {
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

    // Connected by hand rather than through tungstenite's own `connect`, for the same two reasons
    // the macOS agent does it: this is where the connect timeout can be bounded, and
    // `client_tls_with_config` is the only entry point that takes a rustls configuration carrying a
    // client certificate.
    let stream = connect_tcp(host, port, connect_timeout)?;

    // Which address this actually reached. Logged because the URL alone does not say, and on a name
    // that resolves to more than one address — or resolves differently over time — that is the
    // difference between "the server is unreachable" and "this host drew the address it cannot get
    // to". Working that out from the outside cost a support call.
    if let Ok(peer) = stream.peer_addr() {
        logging::info(&format!("connected to {host} at {peer}"));
    }

    // Nagle off: this carries keystrokes and tiles, which matter immediately, and coalescing them
    // into fuller packets trades exactly the latency a remote session is judged on.
    let _ = stream.set_nodelay(true);

    // The handshake is the only blocking read this socket ever does — see `HANDSHAKE_TIMEOUT`,
    // which is what keeps a server that answers nothing from owning this thread for good.
    let _ = stream.set_read_timeout(Some(HANDSHAKE_TIMEOUT));
    let _ = stream.set_write_timeout(Some(HANDSHAKE_TIMEOUT));

    let (socket, _response) = tungstenite::client_tls_with_config(request, stream, None, Some(Connector::Rustls(tls)))
        .map_err(|err| anyhow!("{err}"))
        .with_context(|| format!("the WebSocket handshake with {url} failed"))?;

    clear_handshake_timeouts(&socket);

    Ok(socket)
}

/// Lifts the handshake deadline now that the socket is up.
///
/// `set_nonblocking` is what governs reads from here, and on every platform here it takes precedence
/// over `SO_RCVTIMEO` anyway — but a timeout left set is a second, invisible deadline on a socket
/// that is meant to be idle for hours, and the next reader of this file should not have to work out
/// which of the two wins.
fn clear_handshake_timeouts(socket: &Socket) {
    let stream = match socket.get_ref() {
        MaybeTlsStream::Plain(stream) => Some(stream),
        MaybeTlsStream::Rustls(stream) => Some(&stream.sock),
        _ => None,
    };

    if let Some(stream) = stream {
        let _ = stream.set_read_timeout(None);
        let _ = stream.set_write_timeout(None);
    }
}

/// Connects to the first address of `host` that answers, trying each one the resolver returns.
///
/// Every address, not the first — the same thing `TcpStream::connect` and the check-in's HTTP client
/// do, and the reason a host whose check-ins were fine could still not open a session. A name that
/// resolves to a public address and a private one (split-horizon DNS, or a resolver that knows both)
/// hands them back in rotating order; from inside the network the public one is often unreachable
/// without hairpin NAT. Taking only the first meant the control socket connected whenever it drew
/// the private address, and the session socket timed out whenever it drew the other — a session that
/// consented and then "could not connect", on a host that was plainly reachable.
fn connect_tcp(host: &str, port: u16, timeout: Duration) -> Result<std::net::TcpStream> {
    let addresses: Vec<_> = std::net::ToSocketAddrs::to_socket_addrs(&(host, port))
        .with_context(|| format!("could not resolve {host}"))?
        .collect();
    if addresses.is_empty() {
        return Err(anyhow!("{host} resolved to no addresses"));
    }

    let mut failures = Vec::with_capacity(addresses.len());
    for address in &addresses {
        match std::net::TcpStream::connect_timeout(address, timeout) {
            Ok(stream) => return Ok(stream),
            Err(err) => failures.push(format!("{address}: {err}")),
        }
    }

    Err(anyhow!("could not connect to {host} at any of its addresses ({})", failures.join("; ")))
}

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
/// Arrives two ways and both have to be recognised: straight from the socket, and — with rustls in
/// the way — as tungstenite's own `Io` wrapping it after a partial TLS record. Miss either and an
/// idle socket reads as a broken one, which reconnects in a loop.
fn is_would_block(error: &tungstenite::Error) -> bool {
    match error {
        tungstenite::Error::Io(io) => {
            matches!(io.kind(), std::io::ErrorKind::WouldBlock | std::io::ErrorKind::Interrupted)
        }
        _ => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn config_with(api_base_url: &str) -> Config {
        Config {
            api_base_url: api_base_url.to_string(),
            enrollment_token: None,
        }
    }

    #[test]
    fn the_control_socket_url_carries_only_the_serial_number() {
        assert_eq!(
            config_with("https://kintsugi.example.com:8443").remote_control_url("ABC123", None),
            "wss://kintsugi.example.com:8443/api/remote-control?serialNumber=ABC123"
        );
    }

    #[test]
    fn a_session_socket_url_adds_the_session_id() {
        assert_eq!(
            config_with("https://kintsugi.example.com:8443").remote_control_url("ABC123", Some("s-1")),
            "wss://kintsugi.example.com:8443/api/remote-control?serialNumber=ABC123&sessionId=s-1"
        );
    }

    #[test]
    fn https_becomes_wss_and_http_becomes_ws() {
        // tungstenite refuses an http:// address outright rather than assuming, so the rewrite is
        // required rather than cosmetic.
        assert!(config_with("https://host:8443").remote_control_url("S", None).starts_with("wss://"));
        assert!(config_with("http://host:8080").remote_control_url("S", None).starts_with("ws://"));
    }

    #[test]
    fn the_consent_flush_window_is_shorter_than_the_dialog_it_follows() {
        // The flush happens after the user has already answered, so it must not be able to outlast
        // the server's own patience for the session (90s).
        assert!(CONSENT_FLUSH_TIMEOUT < Duration::from_secs(90));
    }

    #[test]
    fn every_wait_before_the_session_socket_fits_inside_the_server_s_pairing_window() {
        // The agent must run out of attempts before the server runs out of patience, or its own
        // reason — which names the address and the failure — is replaced by the server's "the other
        // end never connected", which names neither.
        let attempts = SESSION_CONNECT_ATTEMPTS;
        let budget = CONSENT_FLUSH_TIMEOUT
            + SESSION_CONNECT_TIMEOUT * attempts
            + SESSION_CONNECT_RETRY_DELAY * (attempts - 1);

        assert!(budget < SERVER_PAIRING_TIMEOUT, "{budget:?} is not inside {SERVER_PAIRING_TIMEOUT:?}");
    }

    #[test]
    fn a_session_connect_attempt_gives_up_sooner_than_the_control_socket_s() {
        // The control socket retries forever on a backoff, so it can afford to wait; a session
        // socket cannot. Equal timeouts would spend the whole pairing window on one attempt.
        assert!(SESSION_CONNECT_TIMEOUT < CONNECT_TIMEOUT);
    }

    #[test]
    fn would_block_is_told_apart_from_a_real_failure() {
        use std::io::ErrorKind;
        assert!(is_would_block(&tungstenite::Error::Io(std::io::Error::from(ErrorKind::WouldBlock))));
        assert!(is_would_block(&tungstenite::Error::Io(std::io::Error::from(ErrorKind::Interrupted))));
        assert!(!is_would_block(&tungstenite::Error::Io(std::io::Error::from(ErrorKind::ConnectionReset))));
        assert!(!is_would_block(&tungstenite::Error::ConnectionClosed));
    }

    /// A socket whose kernel send buffer is full: every write reports `WouldBlock` until `accept`
    /// is set, after which it takes everything. Reads never return anything.
    struct SaturatedStream {
        accept: std::rc::Rc<std::cell::Cell<bool>>,
        taken: Vec<u8>,
    }

    impl Read for SaturatedStream {
        fn read(&mut self, _: &mut [u8]) -> std::io::Result<usize> {
            Err(std::io::ErrorKind::WouldBlock.into())
        }
    }

    impl Write for SaturatedStream {
        fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
            if self.accept.get() {
                self.taken.extend_from_slice(bytes);
                Ok(bytes.len())
            } else {
                Err(std::io::ErrorKind::WouldBlock.into())
            }
        }

        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }

    #[test]
    fn a_tile_burst_the_socket_cannot_take_yet_is_queued_rather_than_fatal() {
        // The shape of the failure that ended every Linux Wayland session on its first frame: enough
        // tile bytes to pass tungstenite's write threshold, into a socket that is not draining.
        let accept = std::rc::Rc::new(std::cell::Cell::new(false));
        let stream = SaturatedStream { accept: accept.clone(), taken: Vec::new() };
        let mut socket = WebSocket::from_raw_socket(stream, tungstenite::protocol::Role::Client, None);

        let tile = vec![0xAB_u8; 64 * 1024];
        for _ in 0..4 {
            write_queued(&mut socket, Message::Binary(tile.clone().into())).expect("WouldBlock is not a failure");
        }
        assert!(socket.get_ref().taken.is_empty(), "nothing can have left while the socket refused every write");

        // The network catches up; the loop's flush step sends what was queued, none of it lost.
        accept.set(true);
        socket.flush().expect("a socket that is draining flushes");
        let sent = &socket.get_ref().taken;
        // Four frames, each a header plus the masked payload.
        assert!(sent.len() >= 4 * tile.len(), "sent {} bytes, expected at least {}", sent.len(), 4 * tile.len());
    }
}
