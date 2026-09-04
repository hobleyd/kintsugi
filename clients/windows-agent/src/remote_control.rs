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
use crate::remote_protocol::{parse_server_message, AgentMessage, ConsentOutcome, ServerMessage};
use crate::session_launcher::{console_session_with_user, SessionHelper};

/// How long the relay waits between polls of its three channels.
const POLL_INTERVAL: Duration = Duration::from_millis(10);

const CONNECT_TIMEOUT: Duration = Duration::from_secs(15);

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
/// # Reachability follows the console session now, not a connected process
///
/// It used to follow the tray process holding the pipe. With capture in a helper that exists only
/// while a session runs, that signal is gone, so the service asks Windows directly:
/// [`console_session_with_user`] answers "is there a console session with somebody logged into it",
/// which is the same question as "is there a screen to share and somebody to ask". The control
/// socket is held open exactly while the answer is yes, so a host with nobody logged in reports as
/// unreachable — the same behaviour as before, arrived at more directly.
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
        let Some(session) = console_session_with_user() else {
            // Nobody logged in. Not an error and not worth backing off over — it is the ordinary
            // state of a server, and the moment somebody signs in the socket opens.
            std::thread::sleep(SESSION_POLL_INTERVAL);
            continue;
        };

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

        match relay(&config, &serial_number, &identity, session, &connection_rx, &shutdown) {
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
    }
}

fn relay(
    config: &Config,
    serial_number: &str,
    identity: &AgentIdentity,
    console_session: u32,
    connections: &Receiver<(PipeConnection, u32)>,
    shutdown: &Arc<AtomicBool>,
) -> Result<()> {
    let url = config.remote_control_url(serial_number, None);
    let mut control = connect(&url, identity)?;
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
    };

    let mut next_session_check = Instant::now() + SESSION_POLL_INTERVAL;

    while !shutdown.load(Ordering::SeqCst) {
        // 0. Has the user logged out from under us? Checked on a timer rather than every pass —
        //    it is two syscalls and it cannot change between frames in any way that matters.
        if Instant::now() >= next_session_check {
            next_session_check = Instant::now() + SESSION_POLL_INTERVAL;
            if console_session_with_user() != Some(console_session) {
                logging::info("the console session ended; closing the remote control socket");
                relay.end_session();
                return Ok(());
            }
        }

        // 1. The server, to the helper.
        match read_text(&mut relay.control)? {
            SocketRead::Message(text) => {
                if let Some(line) =
                    handle_server_message(&text, console_session, connections, &mut relay)?
                {
                    logging::info(&format!("remote control: {line}"));
                }
            }
            SocketRead::Closed => {
                relay.end_session();
                return Ok(());
            }
            SocketRead::Idle => {}
        }

        // 2. The helper, to the server or to the viewer.
        if relay.pipe.is_some() {
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

        // 3. The viewer's input, to the helper.
        if let Some(session) = relay.session.as_mut() {
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
                SocketRead::Idle => {}
            }
        }

        // 4. Push whatever is queued on either socket. Tolerates a partial flush: tungstenite keeps
        //    the remainder and the next pass sends it.
        flush(&mut relay.control)?;
        if let Some(session) = relay.session.as_mut() {
            flush(session)?;
        }

        std::thread::sleep(POLL_INTERVAL);
    }

    relay.end_session();
    Ok(())
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
fn handle_server_message(
    text: &str,
    console_session: u32,
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
        ServerMessage::SessionRequested { session_id, requested_by, consent_timeout_seconds } => {
            if relay.helper.is_some() {
                // One session per host, which the server also enforces. Refusing is the answer that
                // cannot go wrong.
                queue(
                    &mut relay.control,
                    &AgentMessage::Consent { session_id, outcome: ConsentOutcome::Denied },
                )?;
                return Ok(Some("refused a second session on a host already in one".to_string()));
            }

            let line = format!("session {session_id} requested by {requested_by}");

            match start_session_helper(console_session, connections) {
                Ok((mut pipe, helper)) => {
                    write_pipe(
                        &mut pipe,
                        &IpcMessage::SessionRequested {
                            session_id,
                            requested_by,
                            consent_timeout_seconds,
                        },
                    )?;
                    relay.pipe = Some(pipe);
                    relay.helper = Some(helper);
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

            match connect(&config.remote_control_url(serial_number, Some(&session_id)), identity) {
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
                session.write(Message::text(json)).map_err(|err| anyhow!("{err}"))?;
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
                session.write(Message::Binary(tile.into())).map_err(|err| anyhow!("{err}"))?;
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
    Closed,
    Idle,
}

fn read_text(socket: &mut Socket) -> Result<SocketRead> {
    match socket.read() {
        Ok(Message::Text(text)) => Ok(SocketRead::Message(text.to_string())),
        Ok(Message::Close(_)) => Ok(SocketRead::Closed),
        // Ping and pong are answered inside tungstenite. A binary message on either of these
        // sockets is not part of the protocol in this direction and is ignored.
        Ok(_) => Ok(SocketRead::Idle),
        Err(err) if is_would_block(&err) => Ok(SocketRead::Idle),
        Err(tungstenite::Error::ConnectionClosed) | Err(tungstenite::Error::AlreadyClosed) => Ok(SocketRead::Closed),
        Err(err) => Err(anyhow!(err).context("reading from a remote control socket")),
    }
}

fn write_pipe(pipe: &mut PipeConnection, message: &IpcMessage) -> Result<()> {
    pipe.write_all(&remote_ipc::encode_json(message)?)
}

/// Queues a message on a socket. Does not flush — see the macOS agent's `queue` for why a flush that
/// cannot complete right now must not be reported as a failure.
fn queue(socket: &mut Socket, message: &AgentMessage) -> Result<()> {
    let json = serde_json::to_string(message).context("could not serialise a remote control message")?;
    socket
        .write(Message::text(json))
        .map_err(|err| anyhow!("{err}"))
        .context("could not queue a remote control message")
}

/// `Ok(false)` means the socket could not take all of it yet, which is not an error.
fn flush(socket: &mut Socket) -> Result<bool> {
    match socket.flush() {
        Ok(()) => Ok(true),
        Err(err) if is_would_block(&err) => Ok(false),
        Err(err) => Err(anyhow!(err).context("flushing a remote control socket")),
    }
}

/// Opens one `wss://` socket presenting this host's client certificate.
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

    // Connected by hand rather than through tungstenite's own `connect`, for the same two reasons
    // the macOS agent does it: this is where the connect timeout can be bounded, and
    // `client_tls_with_config` is the only entry point that takes a rustls configuration carrying a
    // client certificate.
    let address = std::net::ToSocketAddrs::to_socket_addrs(&(host, port))
        .with_context(|| format!("could not resolve {host}"))?
        .next()
        .with_context(|| format!("{host} resolved to no addresses"))?;

    let stream = std::net::TcpStream::connect_timeout(&address, CONNECT_TIMEOUT)
        .with_context(|| format!("could not connect to {address}"))?;

    // Nagle off: this carries keystrokes and tiles, which matter immediately, and coalescing them
    // into fuller packets trades exactly the latency a remote session is judged on.
    let _ = stream.set_nodelay(true);

    let (socket, _response) = tungstenite::client_tls_with_config(request, stream, None, Some(Connector::Rustls(tls)))
        .map_err(|err| anyhow!("{err}"))
        .with_context(|| format!("the WebSocket handshake with {url} failed"))?;

    Ok(socket)
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
    fn would_block_is_told_apart_from_a_real_failure() {
        use std::io::ErrorKind;
        assert!(is_would_block(&tungstenite::Error::Io(std::io::Error::from(ErrorKind::WouldBlock))));
        assert!(is_would_block(&tungstenite::Error::Io(std::io::Error::from(ErrorKind::Interrupted))));
        assert!(!is_would_block(&tungstenite::Error::Io(std::io::Error::from(ErrorKind::ConnectionReset))));
        assert!(!is_would_block(&tungstenite::Error::ConnectionClosed));
    }
}
