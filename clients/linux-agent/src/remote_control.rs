//! The root side of remote control: the sockets to the server, and the relay onto the local socket.
//!
//! # A fourth systemd unit, and why one was needed
//!
//! The other three root entry points cannot hold a standing connection. `kintsugi-agent.service` is
//! a oneshot on a timer, `kintsugi-agent-queue.service` is a oneshot on a path watch, and the
//! per-user unit holds no identity. Remote control needs a connection the *server* can reach the
//! host through within seconds — an hourly check-in cannot carry "somebody would like to see your
//! screen now" — so this runs as `kintsugi-agent --remote-control` under
//! `kintsugi-agent-remote.service`, resident, restarted by systemd if it dies.
//!
//! It deliberately does **not** take `lock.rs`'s advisory flock. That lock exists so a
//! queue-triggered patch cannot land inside an unattended cycle and deadlock two `apt-get` runs on
//! the dpkg lock. This unit installs nothing — it relays bytes — and taking the lock would mean a
//! remote session blocked patching for as long as somebody was watching.
//!
//! # What this half is and is not
//!
//! For a *screen* session it holds the WebSockets, because it holds this host's identity, and does
//! **nothing else**: no capture, no input, no decision about whether the session may go ahead. Those
//! belong to the per-user process, which has the display. The one message it understands is a
//! granted consent, because that is the moment the session socket has to be opened; everything else
//! is copied between the socket and the local channel without being looked at.
//!
//! For a *shell* session there is no per-user process involved at all. A terminal needs no display,
//! and the shell wanted on a Linux host is root's — which is what this unit already runs as. So the
//! PTY lives here, and nothing crosses `remote_ipc` for it.
//!
//! # The control socket is held whether or not anybody is logged in
//!
//! This used to wait for the per-user process to connect and only then open the socket to the
//! server, which made "reachable for remote control" mean "somebody is sitting at a desktop". That
//! is right for a screen session and quite wrong for a shell: most of a Linux fleet is servers with
//! no graphical session at all, and those are precisely the hosts an administrator wants a terminal
//! on. So the socket is now opened as soon as this host has an identity, a per-user connection is
//! picked up if and when one appears, and a *screen* request arriving without one is answered
//! `Unavailable` — the agent saying "there is nobody here" at once, rather than the host being
//! invisible and the administrator being told the agent is unreachable.
//!
//! This is the Windows agent's `remote_control.rs` with a unix socket in place of a named pipe. The
//! two are meant to read as the same program — see `remote_ipc`.

use std::io::{Read, Write};
use std::sync::mpsc::{self, Receiver};
use std::time::{Duration, Instant};

use anyhow::{anyhow, Context, Result};
use tungstenite::client::IntoClientRequest;
use tungstenite::stream::MaybeTlsStream;
use tungstenite::{Connector, Message, WebSocket};

use crate::config::{self, Config};
use crate::identity::{self, AgentIdentity};
use crate::logging;
use crate::pty::{self, ProgramSpec, Pty, INITIAL_COLS, INITIAL_ROWS};
use crate::remote_ipc::{self, FrameReader, IpcConnection, IpcFrame, IpcListener, IpcMessage};
use crate::remote_protocol::{
    decode_shell_input, encode_shell_output, parse_server_message, parse_viewer_input, AgentMessage,
    ConsentOutcome, ServerMessage, SessionKind, ShellInfo, ViewerInput,
};

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

/// How much terminal output is carried in one shell frame. Same value and reasoning as the macOS
/// agent's: a burst of output arrives as a handful of frames rather than one large one, so it
/// cannot stall the socket the keystrokes travel back on.
const SHELL_READ_BUFFER_BYTES: usize = 32 * 1024;

/// How long the accept thread waits before trying again after a failed accept, so a listener that
/// is briefly unhappy cannot spin.
const ACCEPT_RETRY_DELAY: Duration = Duration::from_secs(5);

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

/// Reconnect backoff. As the other two agents: a host that cannot reach the server is a host nobody
/// can connect to anyway.
const INITIAL_RECONNECT_BACKOFF: Duration = Duration::from_secs(5);
const MAX_RECONNECT_BACKOFF: Duration = Duration::from_secs(60);

/// How long to keep trying to flush a granted consent before opening the session socket anyway.
///
/// The relay refuses a session socket for a grant it has not seen — and refuses it *after* accepting
/// the WebSocket upgrade, so the failure would arrive as a healthy connection that immediately
/// closes, with nothing to retry against. Flushing first is what makes the ordering true rather than
/// likely. Same reasoning and same value as the Windows agent.
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

type Socket = WebSocket<MaybeTlsStream<std::net::TcpStream>>;

/// Runs for the life of the unit. Never returns.
///
/// Stopping is systemd's business: this thread spends most of its life blocked in
/// `IpcListener::accept`, and `SIGTERM` ends the process, taking the socket file with it (see
/// `IpcListener`'s `Drop`, and `create`, which also copes with a file left by a `SIGKILL`).
pub fn run(config: Config, serial_number: String) {
    let socket_path = config::remote_control_socket_path();

    let listener = match IpcListener::create(&socket_path) {
        Ok(listener) => listener,
        Err(err) => {
            logging::error(&format!("remote control is unavailable on this host: {err:#}"));
            return;
        }
    };

    logging::info(&format!("listening for the per-user agent on {}", socket_path.display()));

    // Accepting blocks, and this unit has to hold its socket to the server whether or not anybody
    // is logged in — see the module note. So accepting happens on a thread of its own and hands
    // each connection to the control loop through a channel. The listener moves in with it and
    // lives as long as the process, which is what keeps its `Drop` from removing the socket file
    // out from under a running host.
    let (desktop_tx, desktop_rx) = mpsc::channel();
    std::thread::spawn(move || loop {
        match listener.accept() {
            Ok(connection) => {
                // The receiver is gone only if the control loop has, which it never does.
                if desktop_tx.send(connection).is_err() {
                    return;
                }
            }
            Err(err) => {
                logging::warn(&format!("could not accept a remote control client: {err:#}"));
                std::thread::sleep(ACCEPT_RETRY_DELAY);
            }
        }
    });

    let mut backoff = INITIAL_RECONNECT_BACKOFF;

    loop {
        // Re-read from disk each time rather than taking an identity once: this unit starts before
        // enrollment has necessarily happened, and it outlives a re-enrollment.
        let Some(identity) = identity::load(&config::identity_dir()) else {
            logging::warn("remote control is unavailable until this host has enrolled an identity");
            std::thread::sleep(backoff);
            backoff = (backoff * 2).min(MAX_RECONNECT_BACKOFF);
            continue;
        };

        match relay(&config, &serial_number, &identity, &desktop_rx) {
            Ok(()) => {
                logging::info("the remote control socket was closed by the server; reconnecting");
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

/// Everything in flight while the control socket is up.
struct Relay {
    control: Socket,
    session: Option<Socket>,
    session_id: Option<String>,
    reader: FrameReader,
    /// The terminal, on a shell session and only then. Its presence is what tells the loop which
    /// kind of session is running: a screen session's bytes are relayed to and from the per-user
    /// process, a shell session's to and from this.
    terminal: Option<Pty>,
    /// Why a shell session finished, when [`pump_shell_session`] is the thing that found out —
    /// `exit` typed at the prompt reads very differently from a socket that failed, and the
    /// administrator sees this text.
    shell_end_reason: Option<String>,
}

impl Relay {
    /// Drops whatever session is in flight, hanging up on a terminal if there was one. A shell that
    /// outlived its session would go on holding this host's resources for as long as the unit runs.
    fn end_session(&mut self) {
        self.session = None;
        self.session_id = None;

        if let Some(terminal) = self.terminal.take() {
            if let Err(err) = terminal.terminate() {
                logging::warn(&format!("could not end the terminal: {err}"));
            }
        }
    }
}

fn relay(
    config: &Config,
    serial_number: &str,
    identity: &AgentIdentity,
    desktop_rx: &Receiver<IpcConnection>,
) -> Result<()> {
    let url = config.remote_control_url(serial_number, None);
    let mut control = connect(&url, identity, CONNECT_TIMEOUT)?;
    set_nonblocking(&control)?;

    logging::info(&format!("remote control socket open to {url}"));

    queue(
        &mut control,
        &AgentMessage::Hello {
            agent_version: env!("CARGO_PKG_VERSION").to_string(),
            // The root side does not know which user is at the display, and asking the per-user
            // process for it would be one more message for a diagnostic string.
            console_user: None,
        },
    )?;

    let mut relay = Relay {
        control,
        session: None,
        session_id: None,
        reader: FrameReader::new(),
        terminal: None,
        shell_end_reason: None,
    };

    // The per-user process, once it has connected. `None` on a server with no graphical session,
    // which is an ordinary state here rather than a fault — it only means screen sessions cannot
    // be offered.
    let mut desktop: Option<IpcConnection> = None;

    // The last time anything at all arrived on the control socket — a message or the server's
    // keep-alive ping. See `CONTROL_SILENCE_TIMEOUT`.
    let mut last_heard = Instant::now();

    // Whether the session socket still holds bytes the network has not taken. See step 2.
    let mut session_backlogged = false;
    loop {
        // 0. A per-user process appearing, or reappearing. The latest connection wins: the only way
        //    a second arrives is the first's process having been restarted, and holding on to the
        //    older one would leave screen sessions going to a socket nobody is reading.
        while let Ok(connection) = desktop_rx.try_recv() {
            logging::info("the per-user agent connected; screen sessions are available on this host");
            desktop = Some(connection);
        }

        // 1. The server, to the per-user process or to a terminal here.
        match read_text(&mut relay.control)? {
            SocketRead::Message(text) => {
                last_heard = Instant::now();
                let line = handle_server_message(
                    &text, desktop.as_mut(), config, serial_number, identity, &mut relay)?;
                if let Some(line) = line {
                    logging::info(&format!("remote control: {line}"));
                }
            }
            SocketRead::KeepAlive => last_heard = Instant::now(),
            SocketRead::Closed => return Ok(()),
            SocketRead::Idle => {
                if last_heard.elapsed() > CONTROL_SILENCE_TIMEOUT {
                    return Err(anyhow!(
                        "nothing heard from the server for {}s; treating the control socket as dead",
                        last_heard.elapsed().as_secs()
                    ));
                }
            }
        }

        // 2. The per-user process, to the server or to the viewer.
        //
        // Not drained while the session socket is backlogged. Tiles are produced at the capture rate
        // and leave at the network's, and when the second is slower nothing here may simply keep
        // queueing — tungstenite's out-buffer is unbounded, so that is a growing delay and a growing
        // heap. The macOS agent captures in-process and pauses capture instead; here the capture is
        // in the per-user process, so the relay pauses *reading* and lets the unix socket fill: its
        // `IpcConnection::write_all` blocks until this end reads again, which paces the capture loop
        // to the network without a message in either direction. Step 3 still runs, so a viewer that
        // hangs up mid-backlog is noticed and the backlog discarded with the socket.
        if !session_backlogged {
            if let Some(ipc) = desktop.as_mut() {
                match ipc.read_available() {
                    Ok(chunk) => {
                        if !chunk.is_empty() {
                            relay.reader.push(&chunk);
                        }

                        while let Some(frame) = relay.reader.next_frame()? {
                            handle_agent_frame(frame, config, serial_number, identity, &mut relay)?;
                        }
                    }
                    Err(err) => {
                        // The desktop going away is no longer the end of the relay. It used to be,
                        // because the control socket was only open while a per-user process was —
                        // now losing the display costs this host screen sessions and nothing else,
                        // and it must not cost it its reachability for a shell.
                        logging::info(&format!("the per-user agent disconnected: {err:#}"));
                        desktop = None;
                        relay.reader = FrameReader::new();

                        if let Some(session_id) = relay.session_id.clone() {
                            if relay.terminal.is_none() {
                                relay.end_session();
                                queue(&mut relay.control, &AgentMessage::SessionEnded {
                                    session_id,
                                    reason: "the host's desktop session ended".to_string(),
                                })?;
                            }
                        }
                    }
                }
            }
        }

        // 3. The viewer's input: into the terminal on a shell session, on to the per-user process
        //    on a screen one.
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
                    // Dropped rather than queued when there is no per-user process: it can only
                    // mean the desktop went away mid-session, and the session is being torn down.
                    if let Some(ipc) = desktop.as_mut() {
                        write_ipc(ipc, &IpcMessage::ViewerInput { json })?;
                    }
                }
                SocketRead::Closed => {
                    let session_id = relay.session_id.clone().unwrap_or_default();
                    logging::info("the viewer disconnected");
                    relay.end_session();
                    if let Some(ipc) = desktop.as_mut() {
                        write_ipc(
                            ipc,
                            &IpcMessage::SessionEnded {
                                session_id,
                                reason: "the viewer disconnected".to_string(),
                            },
                        )?;
                    }
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
}

/// Acts on one server message. Returns a line worth logging, if any.
fn handle_server_message(
    text: &str,
    desktop: Option<&mut IpcConnection>,
    config: &Config,
    serial_number: &str,
    identity: &AgentIdentity,
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
            // One session per host, which the server also enforces — but its session table is
            // in-memory and single-process, so an API restart while a session is live loses that
            // knowledge and a second request can arrive here. Refusing is the answer that cannot go
            // wrong, and without it `start_shell_session` would overwrite the live session's socket
            // and terminal and strand both. The other two agents check the same thing.
            if relay.session.is_some() || relay.terminal.is_some() {
                queue(&mut relay.control, &AgentMessage::Consent {
                    session_id,
                    outcome: ConsentOutcome::Denied,
                })?;
                return Ok(Some("refused a second session on a host already in one".to_string()));
            }

            match kind {
                // A terminal needs no display and no consent, and root is what this unit already
                // runs as — so it is started here rather than forwarded anywhere.
                SessionKind::Shell => {
                    let line = format!("shell session {session_id} requested by {requested_by}");
                    start_shell_session(config, serial_number, identity, relay, &session_id, &requested_by)?;
                    Ok(Some(line))
                }

                SessionKind::Screen => match desktop {
                    Some(ipc) => {
                        let line = format!("session {session_id} requested by {requested_by}");
                        write_ipc(
                            ipc,
                            &IpcMessage::SessionRequested { session_id, requested_by, consent_timeout_seconds },
                        )?;
                        Ok(Some(line))
                    }
                    // Nobody is logged in, so there is no screen to share and nobody to ask.
                    // Answered rather than left to time out — see the module note on why this host
                    // is reachable at all in that state.
                    None => {
                        queue(&mut relay.control, &AgentMessage::Consent {
                            session_id: session_id.clone(),
                            outcome: ConsentOutcome::Unavailable,
                        })?;
                        Ok(Some(format!(
                            "refusing screen session {session_id}: nobody is logged in at this host"
                        )))
                    }
                },

                // A kind this build has never heard of. Answered rather than ignored, so the
                // administrator is told now instead of waiting out a consent timeout.
                SessionKind::Unknown => {
                    queue(&mut relay.control, &AgentMessage::Consent {
                        session_id: session_id.clone(),
                        outcome: ConsentOutcome::Unavailable,
                    })?;
                    Ok(Some(format!(
                        "refusing session {session_id}: this agent does not implement that kind of session"
                    )))
                }
            }
        }

        ServerMessage::SessionEnded { session_id, reason } => {
            // Dropped before the per-user process is told, so a frame arriving in the same pass has
            // nowhere to go rather than being written to a socket the server has finished with.
            // `end_session` also hangs up on a terminal, if this was a shell session.
            relay.end_session();
            let line = format!("the server ended session {session_id}: {reason}");
            if let Some(ipc) = desktop {
                write_ipc(ipc, &IpcMessage::SessionEnded { session_id, reason })?;
            }
            Ok(Some(line))
        }
    }
}

/// Starts a root shell and joins it to a session socket.
///
/// # Why nobody is asked
///
/// This is the one place remote control's consent rule does not apply, and it is deliberate. A shell
/// session is the access an administrator of this fleet already has by other means — it is what they
/// would reach SSH for — and the only reason it is routed through this agent is that SSH would need
/// an inbound port on every managed machine, which is exactly what the relay design exists to avoid.
/// The compensating control is the server's: the same `remote_control_sessions` row a screen request
/// writes is written here, naming who asked and when.
///
/// The shell is **root's**, because this unit is root and every upgrade this agent performs already
/// requires it. That is a stronger thing to hand out than the macOS agent's shell, which runs as the
/// logged-in user because the per-user process is the only half of that agent holding the identity;
/// [`ShellInfo`] reports the account for exactly that reason, so the viewer can say which it is.
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
        // fails between the two used to name no program at all.
        logging::info(&format!("starting a remote shell: {} as {user}", program.program.display()));

        let terminal = Pty::spawn(&program, INITIAL_COLS, INITIAL_ROWS)
            .with_context(|| format!("could not start {} in a terminal", program.program.display()))?;
        Ok((program, user, terminal))
    });

    let (program, user, terminal) = match started {
        Ok(started) => started,
        Err(err) => {
            logging::warn(&format!("cannot open a shell session: {err:#}"));
            return queue(&mut relay.control, &AgentMessage::Consent {
                session_id: session_id.to_string(),
                outcome: ConsentOutcome::Unavailable,
            });
        }
    };

    queue(&mut relay.control, &AgentMessage::Consent {
        session_id: session_id.to_string(),
        outcome: ConsentOutcome::NotRequired,
    })?;

    // Flushed before the session socket is opened, the same race the granted path waits out and for
    // the same reason: the relay refuses a session socket for an answer it has not seen, and
    // refuses it *after* accepting the upgrade. See CONSENT_FLUSH_TIMEOUT.
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
            queue(&mut relay.control, &AgentMessage::SessionEnded {
                session_id: session_id.to_string(),
                reason: format!("the host could not open its session socket: {err:#}"),
            })
        }
    }
}

/// Moves one pass of bytes between the terminal and the viewer. `Ok(false)` means the session is
/// over, and [`Relay::shell_end_reason`] says why unless the viewer simply hung up.
///
/// The mirror of the screen path's step 2 and step 3 together, in one function because a terminal
/// is small enough to be both halves: unlike a tile stream there is no backlog to pace, since a
/// shell that produces faster than the network takes it is bounded by the PTY's own buffer.
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

    // The terminal, out to the viewer. Read once per pass rather than drained: a `yes` loop would
    // otherwise never let the input half above run again, and the administrator could not press
    // Ctrl-C.
    let mut buffer = [0u8; SHELL_READ_BUFFER_BYTES];
    match terminal.read_available(&mut buffer) {
        // The shell closed its end — somebody typed `exit`. The ordinary way a session finishes.
        Ok(Some(0)) => {
            relay.shell_end_reason = Some("the shell exited".to_string());
            return Ok(false);
        }
        Ok(Some(read)) => write_queued(session, Message::Binary(encode_shell_output(&buffer[..read]).into()))?,
        Ok(None) => {
            // Checked only once the terminal has nothing left to say, so its last output is on its
            // way first. Needed as well as the EOF above: a shell that exits while a background
            // process still holds the slave open produces no EOF at all.
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

/// Acts on one message from the per-user process.
fn handle_agent_frame(
    frame: IpcFrame,
    config: &Config,
    serial_number: &str,
    identity: &AgentIdentity,
    relay: &mut Relay,
) -> Result<()> {
    match frame {
        IpcFrame::Json(IpcMessage::Consent { session_id, outcome }) => {
            logging::info(&format!("the console user answered session {session_id}: {outcome:?}"));

            queue(&mut relay.control, &AgentMessage::Consent { session_id: session_id.clone(), outcome })?;

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
                    // The per-user process is already capturing at this point, so it has to be told
                    // to stop — otherwise it streams frames into a socket whose other end has
                    // nowhere to put them.
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
            // between the per-user process and the browser.
            if let Some(session) = relay.session.as_mut() {
                write_queued(session, Message::Binary(tile.into()))?;
            }
            Ok(())
        }

        // The three root-to-per-user variants, which never arrive in this direction. Ignored rather
        // than fatal, so a version skew between the two halves cannot take the relay down.
        IpcFrame::Json(other) => {
            logging::warn(&format!("ignoring an unexpected message from the per-user agent: {other:?}"));
            Ok(())
        }
    }
}

/// Accounts whose "shell" exists but refuses to be one.
///
/// Screened by name because `first_existing` only asks whether the path is a file, which every one
/// of these is — so without this the password entry is taken at face value, the terminal starts,
/// the program exits immediately, and the session opens and closes in the same breath reporting
/// "the shell exited". Falling through to the ordinary candidates instead is the useful answer: the
/// process asking already holds whatever privilege it holds, and a locked login shell is a
/// statement about interactive logins rather than about this.
const NOT_A_SHELL: [&str; 5] = [
    "/usr/sbin/nologin",
    "/sbin/nologin",
    "/usr/bin/nologin",
    "/usr/bin/false",
    "/bin/false",
];

/// This process's own login shell, home directory, and the smallest environment a shell needs to
/// behave like one opened by hand.
///
/// Deliberately the *running* user's entry rather than a named one: on this agent that is root,
/// because the resident unit is root, and on the macOS agent the identical function returns the
/// logged-in user because that agent's per-user process is the half holding the identity. One
/// function, two answers, because the two processes genuinely differ — which is exactly the split
/// `pty.rs` describes when it says the choice of program belongs to each agent's `remote_control`.
fn shell_program() -> Result<(ProgramSpec, String)> {
    let (user, home, shell) = passwd_entry().context("could not read this user's password database entry")?;

    // A password entry naming a shell that is not installed is rare but survivable, and so is one
    // naming a refusal like `/usr/sbin/nologin` — see NOT_A_SHELL, which is screened by name because such
    // a path is a perfectly real file and would otherwise be started and exit at once.
    let program = pty::first_existing(&[shell.as_str()])
        .filter(|chosen| !NOT_A_SHELL.contains(&chosen.to_string_lossy().as_ref()))
        .or_else(|| pty::first_existing(&["/bin/bash", "/bin/sh"]))
        .ok_or_else(|| anyhow!("no usable login shell for {user} (the password database names {shell})"))?;

    // A home directory that does not exist would make the shell fail to start at all, which reads
    // as "remote shell is broken on this host" rather than as the misconfiguration it is.
    let cwd = std::path::PathBuf::from(&home);
    let cwd = if cwd.is_dir() { cwd } else { std::path::PathBuf::from("/") };

    Ok((
        ProgramSpec {
            program,
            // A login shell, so the same profile files run that would have on a terminal opened by
            // hand — an administrator's `PATH` and aliases are most of what makes a shell useful.
            args: vec!["-l".to_string()],
            env: vec![
                ("HOME".to_string(), home.clone()),
                ("USER".to_string(), user.clone()),
                ("LOGNAME".to_string(), user.clone()),
                ("SHELL".to_string(), shell),
                // Enough to find the login shell's own startup files; everything past this is the
                // profile's business, which is the point of running one.
                ("PATH".to_string(), "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin".to_string()),
            ],
            cwd,
        },
        user,
    ))
}

/// `(name, home directory, shell)` for the user this process is running as.
fn passwd_entry() -> Result<(String, String, String)> {
    // SAFETY: getpwuid returns a pointer into a static buffer owned by libc, valid until the next
    // call to it on this thread. Everything is copied out before returning, so nothing borrows it.
    unsafe {
        let entry = libc::getpwuid(libc::getuid());
        if entry.is_null() {
            return Err(anyhow!("getpwuid returned nothing for uid {}", libc::getuid()));
        }

        let read = |pointer: *const libc::c_char| -> Result<String> {
            if pointer.is_null() {
                return Err(anyhow!("the password database entry is incomplete"));
            }
            Ok(std::ffi::CStr::from_ptr(pointer).to_string_lossy().into_owned())
        };

        Ok((read((*entry).pw_name)?, read((*entry).pw_dir)?, read((*entry).pw_shell)?))
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
        // control socket's silence watchdog listens for. A binary message is not part of the protocol
        // in this direction and is ignored — but it still counts as the peer talking.
        Ok(_) => Ok(SocketRead::KeepAlive),
        Err(err) if is_would_block(&err) => Ok(SocketRead::Idle),
        Err(tungstenite::Error::ConnectionClosed) | Err(tungstenite::Error::AlreadyClosed) => Ok(SocketRead::Closed),
        Err(err) => Err(anyhow!(err).context("reading from a remote control socket")),
    }
}

fn write_ipc(ipc: &mut IpcConnection, message: &IpcMessage) -> Result<()> {
    ipc.write_all(&remote_ipc::encode_json(message)?)
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
/// the same non-event it is in [`flush`]. Treated as fatal it ended every Wayland session about one
/// second after consent, as "the remote control relay stopped: IO error: Resource temporarily
/// unavailable (os error 11)" on this side and a broken pipe on the per-user side.
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
    // the other agents do it: this is where the connect timeout can be bounded, and
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
        Config { api_base_url: api_base_url.to_string(), enrollment_token: None }
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
    fn the_socket_lives_inside_the_state_directory() {
        // Which is 0711 — traverse-only, so an unprivileged process can reach this known path and
        // still cannot list the identity beside it.
        assert!(config::remote_control_socket_path().starts_with(config::state_dir()));
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
        // The shape of the failure that ended every Wayland session on its first frame: enough
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
