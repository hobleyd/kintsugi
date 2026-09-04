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
//! It holds the WebSockets, because it holds this host's identity, and it does **nothing else**: no
//! capture, no input, no decision about whether a session may go ahead. Those belong to the per-user
//! process, which has the display. The one message it understands is a granted consent, because that
//! is the moment the session socket has to be opened; everything else is copied between the socket
//! and the local channel without being looked at.
//!
//! This is the Windows agent's `remote_control.rs` with a unix socket in place of a named pipe. The
//! two are meant to read as the same program — see `remote_ipc`.

use std::time::{Duration, Instant};

use anyhow::{anyhow, Context, Result};
use tungstenite::client::IntoClientRequest;
use tungstenite::stream::MaybeTlsStream;
use tungstenite::{Connector, Message, WebSocket};

use crate::config::{self, Config};
use crate::identity::{self, AgentIdentity};
use crate::logging;
use crate::remote_ipc::{self, FrameReader, IpcConnection, IpcFrame, IpcListener, IpcMessage};
use crate::remote_protocol::{parse_server_message, AgentMessage, ConsentOutcome, ServerMessage};

/// How long the relay waits between polls of its three channels.
const POLL_INTERVAL: Duration = Duration::from_millis(10);

const CONNECT_TIMEOUT: Duration = Duration::from_secs(15);

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

    let mut backoff = INITIAL_RECONNECT_BACKOFF;

    loop {
        let connection = match listener.accept() {
            Ok(connection) => connection,
            Err(err) => {
                logging::warn(&format!("could not accept a remote control client: {err:#}"));
                std::thread::sleep(backoff);
                backoff = (backoff * 2).min(MAX_RECONNECT_BACKOFF);
                continue;
            }
        };

        // Re-read from disk each time rather than taking an identity once: this unit starts before
        // enrollment has necessarily happened, and it outlives a re-enrollment.
        let Some(identity) = identity::load(&config::identity_dir()) else {
            logging::warn("remote control is unavailable until this host has enrolled an identity");
            std::thread::sleep(backoff);
            backoff = (backoff * 2).min(MAX_RECONNECT_BACKOFF);
            continue;
        };

        match relay(&config, &serial_number, &identity, connection) {
            Ok(()) => {
                logging::info("the remote control connection closed; waiting for the per-user agent again");
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

/// Everything in flight for one per-user connection.
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
    mut ipc: IpcConnection,
) -> Result<()> {
    let url = config.remote_control_url(serial_number, None);
    let mut control = connect(&url, identity)?;
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

    let mut relay = Relay { control, session: None, session_id: None, reader: FrameReader::new() };

    loop {
        // 1. The server, to the per-user process.
        match read_text(&mut relay.control)? {
            SocketRead::Message(text) => {
                if let Some(line) = handle_server_message(&text, &mut ipc, &mut relay)? {
                    logging::info(&format!("remote control: {line}"));
                }
            }
            SocketRead::Closed => return Ok(()),
            SocketRead::Idle => {}
        }

        // 2. The per-user process, to the server or to the viewer.
        let chunk = ipc.read_available().context("the per-user agent's connection closed")?;
        if !chunk.is_empty() {
            relay.reader.push(&chunk);
        }

        while let Some(frame) = relay.reader.next_frame()? {
            handle_agent_frame(frame, config, serial_number, identity, &mut relay)?;
        }

        // 3. The viewer's input, to the per-user process.
        if let Some(session) = relay.session.as_mut() {
            match read_text(session)? {
                SocketRead::Message(json) => write_ipc(&mut ipc, &IpcMessage::ViewerInput { json })?,
                SocketRead::Closed => {
                    let session_id = relay.session_id.clone().unwrap_or_default();
                    logging::info("the viewer disconnected");
                    relay.session = None;
                    relay.session_id = None;
                    write_ipc(
                        &mut ipc,
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
}

/// Forwards one server message onward. Returns a line worth logging, if any.
fn handle_server_message(text: &str, ipc: &mut IpcConnection, relay: &mut Relay) -> Result<Option<String>> {
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
            write_ipc(
                ipc,
                &IpcMessage::SessionRequested { session_id, requested_by, consent_timeout_seconds },
            )?;
            Ok(Some(line))
        }

        ServerMessage::SessionEnded { session_id, reason } => {
            // Dropped before the per-user process is told, so a frame arriving in the same pass has
            // nowhere to go rather than being written to a socket the server has finished with.
            relay.session = None;
            relay.session_id = None;
            let line = format!("the server ended session {session_id}: {reason}");
            write_ipc(ipc, &IpcMessage::SessionEnded { session_id, reason })?;
            Ok(Some(line))
        }
    }
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

            match connect(&config.remote_control_url(serial_number, Some(&session_id)), identity) {
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
            // between the per-user process and the browser.
            if let Some(session) = relay.session.as_mut() {
                session.write(Message::Binary(tile.into())).map_err(|err| anyhow!("{err}"))?;
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

enum SocketRead {
    Message(String),
    Closed,
    Idle,
}

fn read_text(socket: &mut Socket) -> Result<SocketRead> {
    match socket.read() {
        Ok(Message::Text(text)) => Ok(SocketRead::Message(text.to_string())),
        Ok(Message::Close(_)) => Ok(SocketRead::Closed),
        // Ping and pong are answered inside tungstenite. A binary message is not part of the
        // protocol in this direction and is ignored.
        Ok(_) => Ok(SocketRead::Idle),
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
    // the other agents do it: this is where the connect timeout can be bounded, and
    // `client_tls_with_config` is the only entry point that takes a rustls configuration carrying a
    // client certificate.
    let stream = connect_tcp(host, port)?;

    // Nagle off: this carries keystrokes and tiles, which matter immediately, and coalescing them
    // into fuller packets trades exactly the latency a remote session is judged on.
    let _ = stream.set_nodelay(true);

    let (socket, _response) = tungstenite::client_tls_with_config(request, stream, None, Some(Connector::Rustls(tls)))
        .map_err(|err| anyhow!("{err}"))
        .with_context(|| format!("the WebSocket handshake with {url} failed"))?;

    Ok(socket)
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
fn connect_tcp(host: &str, port: u16) -> Result<std::net::TcpStream> {
    let addresses: Vec<_> = std::net::ToSocketAddrs::to_socket_addrs(&(host, port))
        .with_context(|| format!("could not resolve {host}"))?
        .collect();
    if addresses.is_empty() {
        return Err(anyhow!("{host} resolved to no addresses"));
    }

    let mut failures = Vec::with_capacity(addresses.len());
    for address in &addresses {
        match std::net::TcpStream::connect_timeout(address, CONNECT_TIMEOUT) {
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
    fn would_block_is_told_apart_from_a_real_failure() {
        use std::io::ErrorKind;
        assert!(is_would_block(&tungstenite::Error::Io(std::io::Error::from(ErrorKind::WouldBlock))));
        assert!(is_would_block(&tungstenite::Error::Io(std::io::Error::from(ErrorKind::Interrupted))));
        assert!(!is_would_block(&tungstenite::Error::Io(std::io::Error::from(ErrorKind::ConnectionReset))));
        assert!(!is_would_block(&tungstenite::Error::ConnectionClosed));
    }
}
