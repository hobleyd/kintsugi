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

type Socket = WebSocket<MaybeTlsStream<std::net::TcpStream>>;

/// Runs for the life of the service. Never returns until `shutdown` is set between connections.
///
/// A note on stopping: this thread spends most of its life blocked in `PipeListener::accept`, which
/// the Service Control Manager's stop cannot interrupt. That is deliberate rather than overlooked —
/// the service's own `run_loop` returns promptly, `windows-service` reports STOPPED, and the process
/// exits, taking this thread with it. Making the accept interruptible would mean overlapped I/O and
/// an event object for no behavioural gain.
pub fn run(config: Config, serial_number: String, shutdown: Arc<AtomicBool>) {
    let listener = match PipeListener::create() {
        Ok(listener) => listener,
        Err(err) => {
            // Not fatal to the service: everything else it does still works, and remote control is
            // the only thing lost. Reported loudly because nothing else will explain why the Hosts
            // screen reports this host as unreachable.
            logging::error(&format!("remote control is unavailable on this host: {err:#}"));
            return;
        }
    };

    let mut backoff = INITIAL_RECONNECT_BACKOFF;

    while !shutdown.load(Ordering::SeqCst) {
        let pipe = match listener.accept() {
            Ok(pipe) => pipe,
            Err(err) => {
                logging::warn(&format!("could not accept a remote control pipe client: {err:#}"));
                std::thread::sleep(backoff);
                backoff = (backoff * 2).min(MAX_RECONNECT_BACKOFF);
                continue;
            }
        };

        logging::info("the console session's tray process connected for remote control");

        // Re-read from disk each time rather than taking an identity once. The service starts before
        // enrollment has necessarily happened, and it outlives a re-enrollment.
        //
        // `load` returns a Result here, unlike the macOS agent's: on Windows an identity that
        // cannot be *read* is told apart from one that does not exist, because the two need
        // completely different fixes and conflating them once had this agent re-enrolling on every
        // check-in forever. Both mean no remote control, but they are logged differently.
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

        match relay(&config, &serial_number, &identity, pipe, &shutdown) {
            Ok(()) => {
                logging::info("the remote control connection closed; waiting for the tray process again");
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

/// Everything in flight for one tray connection.
struct Relay {
    control: Socket,
    session: Option<Socket>,
    session_id: Option<String>,
    reader: FrameReader,
}

fn relay(
    config: &Config,
    serial_number: &str,
    identity: &AgentIdentity,
    mut pipe: PipeConnection,
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

    let mut relay = Relay { control, session: None, session_id: None, reader: FrameReader::new() };

    while !shutdown.load(Ordering::SeqCst) {
        // 1. The server, to the tray.
        match read_text(&mut relay.control)? {
            SocketRead::Message(text) => {
                if let Some(message) = handle_server_message(&text, &mut pipe, &mut relay)? {
                    logging::info(&format!("remote control: {message}"));
                }
            }
            SocketRead::Closed => return Ok(()),
            SocketRead::Idle => {}
        }

        // 2. The tray, to the server or to the viewer.
        let chunk = pipe.read_available().context("the tray process's pipe closed")?;
        if !chunk.is_empty() {
            relay.reader.push(&chunk);
        }

        while let Some(frame) = relay.reader.next_frame()? {
            handle_tray_frame(frame, config, serial_number, identity, &mut relay)?;
        }

        // 3. The viewer's input, to the tray.
        if let Some(session) = relay.session.as_mut() {
            match read_text(session)? {
                SocketRead::Message(json) => {
                    write_pipe(&mut pipe, &IpcMessage::ViewerInput { json })?;
                }
                SocketRead::Closed => {
                    let session_id = relay.session_id.clone().unwrap_or_default();
                    logging::info("the viewer disconnected");
                    relay.session = None;
                    relay.session_id = None;
                    write_pipe(
                        &mut pipe,
                        &IpcMessage::SessionEnded {
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

    Ok(())
}

/// Forwards one server message to the tray. Returns a line worth logging, if any.
fn handle_server_message(text: &str, pipe: &mut PipeConnection, relay: &mut Relay) -> Result<Option<String>> {
    let message = match parse_server_message(text) {
        Ok(Some(message)) => message,
        // A newer server mentioning something this build has never heard of. Logged and ignored,
        // exactly as the server treats an unrecognised message from an agent.
        Ok(None) => return Ok(Some("ignoring an unrecognised message from the server".to_string())),
        Err(err) => return Ok(Some(format!("could not read a message from the server: {err}"))),
    };

    match message {
        ServerMessage::SessionRequested { session_id, requested_by, consent_timeout_seconds } => {
            let line = format!("session {session_id} requested by {requested_by}");
            write_pipe(
                pipe,
                &IpcMessage::SessionRequested { session_id, requested_by, consent_timeout_seconds },
            )?;
            Ok(Some(line))
        }

        ServerMessage::SessionEnded { session_id, reason } => {
            // Dropped before the tray is told, so a frame arriving in the same pass has nowhere to
            // go rather than being written to a socket the server has finished with.
            relay.session = None;
            relay.session_id = None;
            let line = format!("the server ended session {session_id}: {reason}");
            write_pipe(pipe, &IpcMessage::SessionEnded { session_id, reason })?;
            Ok(Some(line))
        }
    }
}

/// Acts on one message from the tray.
fn handle_tray_frame(
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

        // The three service-to-tray variants, which the tray never sends back. Ignored rather than
        // fatal, so a version skew between the two halves cannot take the relay down.
        IpcFrame::Json(other) => {
            logging::warn(&format!("ignoring an unexpected message from the tray process: {other:?}"));
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
