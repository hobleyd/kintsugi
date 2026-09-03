//! The local channel between the root service and the per-user process.
//!
//! # Why this exists, and why it is the same shape as the Windows agent's
//!
//! The per-user process holds no identity and makes no network call at all — that is the whole
//! design of this agent, and the reason its 0.5.0 attempt to fetch the patching policy itself
//! 403'd once a minute forever (see `policy`). So it cannot open the remote control socket. The root
//! side can, because it holds the identity, and cannot capture a screen or post input, because
//! those need the graphical session's display and authority.
//!
//! Neither half can do this alone, exactly as on Windows, so the split is the same and the file it
//! most resembles is `clients/windows-agent/src/remote_ipc.rs`. Only the primitive differs: a unix
//! domain socket rather than a named pipe.
//!
//! Read the two side by side and the framing, the message set and the reassembly are identical. That
//! is worth keeping: the two privileged relays are then the same program, and a fix to one is
//! obviously a fix to the other.
//!
//! # What a hostile local process could do, and the check that narrows it
//!
//! The socket has to be reachable by an unprivileged user, so a process that connected first could
//! answer a consent request itself and then feed the administrator a fabricated screen while
//! swallowing their keystrokes.
//!
//! Windows narrows this by checking the client's session against the active console session. Linux
//! has something better available: the root service can read `/proc/<pid>/exe` for *any* process, so
//! [`accept`] resolves the peer's own executable through `SO_PEERCRED` and refuses anything that is
//! not the installed agent binary. An attacker would have to get the real agent to connect on their
//! behalf rather than simply connecting.
//!
//! What it still does not defend against — and neither does the Windows check — is an attacker
//! already executing code *as the logged-in user*, who can read that user's screen and keyboard
//! directly and gains nothing by going through this socket.
//!
//! # The socket's permissions
//!
//! The socket sits inside the state directory, which is `0711` — traverse-only, so an unprivileged
//! process can reach a known path inside it and cannot list it. The socket itself is `0666`, because
//! there is no group to restrict it to: "local administrators" is `sudo` on Debian and `wheel` on
//! Red Hat and neither elsewhere, which is the same reason the request queue is a `1733` drop-box
//! with no group rather than the macOS agent's `root:admin 0770`.

use std::io::{ErrorKind, Read, Write};
use std::os::unix::net::{UnixListener, UnixStream};
use std::path::{Path, PathBuf};

use anyhow::{anyhow, Context, Result};
use serde::{Deserialize, Serialize};

use crate::remote_protocol::ConsentOutcome;

/// Message kinds on the wire. Two, because the media protocol's own text/binary distinction has to
/// survive the trip — the viewer relies on it (JSON control vs JPEG tile), and a byte stream has no
/// framing of its own to carry it.
const KIND_JSON: u8 = 1;
const KIND_TILE: u8 = 2;

/// Frame header: kind byte plus a big-endian length.
const HEADER_BYTES: usize = 5;

/// The largest single message the socket will carry. A full-screen JPEG at the quality this agent
/// uses is a few hundred kilobytes; this is generous next to that and bounds what a malformed length
/// field can make either side allocate.
const MAX_MESSAGE_BYTES: u32 = 8 * 1024 * 1024;

/// Everything the two halves say to each other. Identical to the Windows agent's.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "type")]
pub enum IpcMessage {
    /// Service to per-user: the server is asking for this host. Put the consent dialog up.
    #[serde(rename = "session-requested")]
    SessionRequested {
        #[serde(rename = "sessionId")]
        session_id: String,
        #[serde(rename = "requestedBy")]
        requested_by: String,
        #[serde(rename = "consentTimeoutSeconds")]
        consent_timeout_seconds: u64,
    },

    /// Service to per-user: stop capturing.
    #[serde(rename = "session-ended")]
    SessionEnded {
        #[serde(rename = "sessionId")]
        session_id: String,
        reason: String,
    },

    /// Service to per-user: one pointer, key or quality message, exactly as the viewer sent it.
    /// Passed through as an opaque string — the service has no reason to understand input, and
    /// parsing it would be a second place for the media protocol to drift.
    #[serde(rename = "viewer-input")]
    ViewerInput { json: String },

    /// Per-user to service: what the person at the keyboard said.
    #[serde(rename = "consent")]
    Consent {
        #[serde(rename = "sessionId")]
        session_id: String,
        outcome: ConsentOutcome,
    },

    /// Per-user to service: the display geometry, forwarded to the viewer as a text message.
    #[serde(rename = "display")]
    DisplayInfo { json: String },

    /// Per-user to service: the person at the keyboard ended it, or capture failed.
    #[serde(rename = "ended-by-host")]
    EndedByHost {
        #[serde(rename = "sessionId")]
        session_id: String,
        reason: String,
    },
}

/// A whole message read off the socket. Tiles are kept out of [`IpcMessage`] so a few hundred
/// kilobytes of JPEG never goes near a JSON encoder.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum IpcFrame {
    Json(IpcMessage),
    /// Per-user to service: one JPEG tile, already framed for the media protocol by
    /// `remote_protocol::encode_tile`. Relayed to the viewer as a binary message, unexamined.
    Tile(Vec<u8>),
}

pub fn encode_json(message: &IpcMessage) -> Result<Vec<u8>> {
    let payload = serde_json::to_vec(message).context("could not serialise a remote control IPC message")?;
    Ok(frame(KIND_JSON, &payload))
}

pub fn encode_tile(tile: &[u8]) -> Vec<u8> {
    frame(KIND_TILE, tile)
}

fn frame(kind: u8, payload: &[u8]) -> Vec<u8> {
    let mut message = Vec::with_capacity(HEADER_BYTES + payload.len());
    message.push(kind);
    message.extend_from_slice(&(payload.len() as u32).to_be_bytes());
    message.extend_from_slice(payload);
    message
}

/// Reassembles whole messages from a byte stream.
///
/// A stream socket delivers whatever happens to have arrived, so a read can hand back half a header
/// or three messages at once. Identical to the Windows agent's, deliberately.
#[derive(Debug, Default)]
pub struct FrameReader {
    buffer: Vec<u8>,
}

impl FrameReader {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn push(&mut self, bytes: &[u8]) {
        self.buffer.extend_from_slice(bytes);
    }

    /// The next complete message, or `Ok(None)` while one is still arriving. `Err` means the stream
    /// is unusable and the connection should be dropped — there is no resynchronising a byte stream
    /// whose framing is not understood.
    pub fn next_frame(&mut self) -> Result<Option<IpcFrame>> {
        if self.buffer.len() < HEADER_BYTES {
            return Ok(None);
        }

        let kind = self.buffer[0];
        let length = u32::from_be_bytes([self.buffer[1], self.buffer[2], self.buffer[3], self.buffer[4]]);

        if length > MAX_MESSAGE_BYTES {
            return Err(anyhow!("a remote control IPC message claimed to be {length} bytes"));
        }

        let total = HEADER_BYTES + length as usize;
        if self.buffer.len() < total {
            return Ok(None);
        }

        let payload = self.buffer[HEADER_BYTES..total].to_vec();
        self.buffer.drain(..total);

        match kind {
            KIND_JSON => {
                let message = serde_json::from_slice::<IpcMessage>(&payload)
                    .context("could not read a remote control IPC message")?;
                Ok(Some(IpcFrame::Json(message)))
            }
            KIND_TILE => Ok(Some(IpcFrame::Tile(payload))),
            other => Err(anyhow!("unrecognised remote control IPC message kind {other}")),
        }
    }
}

/// One connected socket, read and written from a single thread.
///
/// Non-blocking, so one loop can interleave reading input with sending frames. Unlike the Windows
/// pipe this needs no `PeekNamedPipe` dance: a unix socket can simply be put into non-blocking mode
/// and a read that has nothing returns `WouldBlock`.
pub struct IpcConnection {
    stream: UnixStream,
}

impl IpcConnection {
    fn adopt(stream: UnixStream) -> Result<Self> {
        stream
            .set_nonblocking(true)
            .context("could not set the remote control socket non-blocking")?;
        Ok(Self { stream })
    }

    /// The per-user side: connects to the socket the root service is listening on.
    pub fn connect(path: &Path) -> Result<Self> {
        let stream = UnixStream::connect(path)
            .with_context(|| format!("could not connect to {}", path.display()))?;
        Self::adopt(stream)
    }

    /// Whatever has arrived, or an empty vector if nothing has.
    pub fn read_available(&mut self) -> Result<Vec<u8>> {
        let mut buffer = [0u8; 64 * 1024];

        match self.stream.read(&mut buffer) {
            // A stream socket returning zero means the peer closed, which is not the same as
            // "nothing to read" and must not be mistaken for it — otherwise a closed connection
            // spins this loop forever.
            Ok(0) => Err(anyhow!("the remote control socket was closed by the other end")),
            Ok(read) => Ok(buffer[..read].to_vec()),
            Err(err) if err.kind() == ErrorKind::WouldBlock => Ok(Vec::new()),
            Err(err) if err.kind() == ErrorKind::Interrupted => Ok(Vec::new()),
            Err(err) => Err(err).context("could not read from the remote control socket"),
        }
    }

    /// Writes a whole message, blocking until it is all out.
    ///
    /// A short write on a non-blocking socket has to be retried rather than dropped: half a JPEG
    /// tile followed by the next message's header is a stream neither side can resynchronise. The
    /// loop is bounded only by the peer draining, which it does — the other end's own loop reads
    /// every pass.
    pub fn write_all(&mut self, bytes: &[u8]) -> Result<()> {
        let mut written = 0;

        while written < bytes.len() {
            match self.stream.write(&bytes[written..]) {
                Ok(0) => return Err(anyhow!("the remote control socket stopped accepting writes")),
                Ok(count) => written += count,
                Err(err) if matches!(err.kind(), ErrorKind::WouldBlock | ErrorKind::Interrupted) => {
                    // The peer is behind. Yielding rather than spinning, and briefly rather than
                    // for a frame interval: this is the path a burst of tiles takes.
                    std::thread::sleep(std::time::Duration::from_millis(1));
                }
                Err(err) => return Err(err).context("could not write to the remote control socket"),
            }
        }

        Ok(())
    }
}

/// The root service's end: owns the socket file and accepts the per-user process.
pub struct IpcListener {
    listener: UnixListener,
    path: PathBuf,
}

impl IpcListener {
    /// Creates the socket, replacing a stale one left by a previous run.
    ///
    /// A unix socket file outlives the process that bound it, and `bind` fails with
    /// `AddrInUse` on an existing path whether or not anything is listening — so a service killed
    /// rather than stopped would never get its socket back without this.
    pub fn create(path: &Path) -> Result<Self> {
        if path.exists() {
            std::fs::remove_file(path)
                .with_context(|| format!("could not remove the stale socket at {}", path.display()))?;
        }

        let listener = UnixListener::bind(path)
            .with_context(|| format!("could not create the remote control socket at {}", path.display()))?;

        // 0666 on the socket, inside a 0711 directory — see the module note on why there is no group
        // to use instead. What actually constrains who may speak here is the peer check in `accept`.
        set_mode(path, 0o666)?;

        Ok(Self { listener, path: path.to_path_buf() })
    }

    /// Blocks until the per-user process connects, and returns it only if the peer really is this
    /// agent's own binary.
    ///
    /// A rejected peer is dropped and the wait resumes, so a process that is not the agent cannot
    /// hold the socket open and starve the one that is.
    pub fn accept(&self) -> Result<IpcConnection> {
        loop {
            let (stream, _address) = self
                .listener
                .accept()
                .context("could not accept a remote control socket client")?;

            match peer_is_this_agent(&stream) {
                Ok(true) => return IpcConnection::adopt(stream),
                Ok(false) => crate::logging::warn(
                    "a remote control socket client that is not this agent's own binary was refused",
                ),
                Err(err) => crate::logging::warn(&format!(
                    "could not establish what connected to the remote control socket: {err:#}"
                )),
            }
        }
    }
}

impl Drop for IpcListener {
    fn drop(&mut self) {
        // Removed on the way out so the next start does not have to clean up after this one. Best
        // effort: `create` copes with a leftover file anyway, which is what covers a kill -9.
        let _ = std::fs::remove_file(&self.path);
    }
}

/// Whether the process at the other end of `stream` is running this agent's own installed binary.
///
/// `SO_PEERCRED` is the kernel's own answer about who connected — it cannot be forged by the peer,
/// unlike anything the peer might say about itself — and `/proc/<pid>/exe` is readable by root for
/// any process, so the check is available here and would not be from the other side.
///
/// There is a theoretical race: the pid could exit and be reused between `accept` and the readlink.
/// It is not worth defending — pid reuse on that timescale requires wrapping the whole pid space in
/// microseconds — and the alternative would be `pidfd` plumbing for no practical gain.
fn peer_is_this_agent(stream: &UnixStream) -> Result<bool> {
    let (pid, uid) = peer_credentials(stream)?;

    let peer_exe = std::fs::read_link(format!("/proc/{pid}/exe"))
        .with_context(|| format!("could not read /proc/{pid}/exe"))?;

    let expected = crate::config::installed_binary_path();

    // Compared after canonicalising both, so a symlinked install path (or a `/usr/local/bin` that is
    // itself a link) does not read as an impostor.
    let peer_exe = peer_exe.canonicalize().unwrap_or(peer_exe);
    let expected = expected.canonicalize().unwrap_or(expected);

    if peer_exe != expected {
        crate::logging::warn(&format!(
            "remote control socket client is {} rather than {}",
            peer_exe.display(),
            expected.display()
        ));
        return Ok(false);
    }

    crate::logging::info(&format!(
        "the per-user agent connected for remote control (uid {uid}, pid {pid})"
    ));

    Ok(true)
}

/// The peer's process and user id, straight from the kernel.
///
/// `getsockopt(SO_PEERCRED)` by hand rather than `UnixStream::peer_cred`, which is still unstable
/// after eight years — and this agent builds on stable Rust like everything else in the repository.
/// `libc` is already a dependency, so it costs one small unsafe block rather than a nightly
/// toolchain.
///
/// This is the kernel's own answer about who connected, recorded at connect time and unforgeable by
/// the peer — which is the whole reason it is worth asking, rather than trusting anything the peer
/// says about itself.
///
/// **Gated on Linux purely so this crate still type-checks on a developer's Mac.** `libc::ucred` and
/// `SO_PEERCRED` exist only on Linux, and before remote control this whole agent happened to
/// `cargo build` on macOS — which is worth keeping, because it is the fastest way to check a change
/// compiles without waiting on a container. The stub below can never run: the only thing that calls
/// it is the root unit, and there is no root unit anywhere but Linux.
#[cfg(target_os = "linux")]
fn peer_credentials(stream: &UnixStream) -> Result<(i32, u32)> {
    use std::os::fd::AsRawFd;

    let mut credentials = libc::ucred { pid: 0, uid: 0, gid: 0 };
    let mut length = std::mem::size_of::<libc::ucred>() as libc::socklen_t;

    // SAFETY: a connected unix socket's fd, a correctly-sized out-parameter of the type
    // SO_PEERCRED is documented to write, and its length passed by pointer as required.
    let result = unsafe {
        libc::getsockopt(
            stream.as_raw_fd(),
            libc::SOL_SOCKET,
            libc::SO_PEERCRED,
            &mut credentials as *mut libc::ucred as *mut libc::c_void,
            &mut length,
        )
    };

    if result != 0 {
        return Err(std::io::Error::last_os_error()).context("SO_PEERCRED is unavailable on this socket");
    }

    if credentials.pid <= 0 {
        return Err(anyhow!("the kernel reported no process id for the peer"));
    }

    Ok((credentials.pid, credentials.uid))
}

/// See the Linux implementation above: this exists so the crate compiles off Linux and is
/// unreachable in any build that can actually run the root unit.
#[cfg(not(target_os = "linux"))]
fn peer_credentials(_stream: &UnixStream) -> Result<(i32, u32)> {
    Err(anyhow!("SO_PEERCRED is a Linux facility and this agent only runs its root half on Linux"))
}

fn set_mode(path: &Path, mode: u32) -> Result<()> {
    use std::os::unix::fs::PermissionsExt;

    std::fs::set_permissions(path, std::fs::Permissions::from_mode(mode))
        .with_context(|| format!("could not set mode {mode:o} on {}", path.display()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_json_message_survives_the_round_trip() {
        let message = IpcMessage::SessionRequested {
            session_id: "abc".to_string(),
            requested_by: "admin@example.com".to_string(),
            consent_timeout_seconds: 90,
        };

        let mut reader = FrameReader::new();
        reader.push(&encode_json(&message).unwrap());

        assert_eq!(reader.next_frame().unwrap(), Some(IpcFrame::Json(message)));
        assert_eq!(reader.next_frame().unwrap(), None);
    }

    #[test]
    fn a_tile_survives_the_round_trip_byte_for_byte() {
        // Tiles are already framed for the media protocol by the time they get here, so anything
        // this layer did to them would corrupt the picture.
        let tile = vec![1, 1, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 1, 0xFF, 0xD8];

        let mut reader = FrameReader::new();
        reader.push(&encode_tile(&tile));

        assert_eq!(reader.next_frame().unwrap(), Some(IpcFrame::Tile(tile)));
    }

    #[test]
    fn a_message_split_across_reads_is_reassembled() {
        let encoded = encode_json(&IpcMessage::SessionEnded {
            session_id: "abc".to_string(),
            reason: "the administrator disconnected".to_string(),
        })
        .unwrap();

        let mut reader = FrameReader::new();
        for byte in &encoded {
            // One byte at a time is the worst case, and every prefix must read as "not yet".
            assert_eq!(reader.next_frame().unwrap(), None);
            reader.push(&[*byte]);
        }

        assert!(matches!(reader.next_frame().unwrap(), Some(IpcFrame::Json(_))));
    }

    #[test]
    fn several_messages_in_one_read_all_come_out() {
        let mut bytes = encode_json(&IpcMessage::Consent {
            session_id: "abc".to_string(),
            outcome: ConsentOutcome::Granted,
        })
        .unwrap();
        bytes.extend_from_slice(&encode_tile(&[0xFF, 0xD8]));
        bytes.extend_from_slice(&encode_json(&IpcMessage::DisplayInfo { json: "{}".to_string() }).unwrap());

        let mut reader = FrameReader::new();
        reader.push(&bytes);

        assert!(matches!(reader.next_frame().unwrap(), Some(IpcFrame::Json(IpcMessage::Consent { .. }))));
        assert!(matches!(reader.next_frame().unwrap(), Some(IpcFrame::Tile(_))));
        assert!(matches!(reader.next_frame().unwrap(), Some(IpcFrame::Json(IpcMessage::DisplayInfo { .. }))));
        assert_eq!(reader.next_frame().unwrap(), None);
    }

    #[test]
    fn an_absurd_length_is_refused_rather_than_allocated() {
        // Otherwise a corrupted or hostile header is an out-of-memory abort in whichever half read
        // it, and on the service side that is the process holding this host's identity.
        let mut reader = FrameReader::new();
        reader.push(&[KIND_TILE, 0xFF, 0xFF, 0xFF, 0xFF]);

        assert!(reader.next_frame().is_err());
    }

    #[test]
    fn an_unknown_kind_byte_is_fatal_rather_than_skipped() {
        let mut reader = FrameReader::new();
        reader.push(&[99, 0, 0, 0, 1, b'x']);

        assert!(reader.next_frame().is_err());
    }

    #[test]
    fn malformed_json_is_fatal_rather_than_skipped() {
        let mut reader = FrameReader::new();
        reader.push(&frame(KIND_JSON, b"not json"));

        assert!(reader.next_frame().is_err());
    }

    #[test]
    fn consent_outcomes_cross_the_socket_by_name() {
        // The service forwards this straight into the control socket's `consent` message, where the
        // server parses it into RemoteControlConsent by name.
        let encoded = serde_json::to_string(&IpcMessage::Consent {
            session_id: "abc".to_string(),
            outcome: ConsentOutcome::TimedOut,
        })
        .unwrap();

        assert!(encoded.contains(r#""outcome":"TimedOut""#), "{encoded}");
    }

    #[test]
    fn the_framing_matches_the_windows_agents_byte_for_byte() {
        // Both privileged relays are meant to be the same program. A header written differently on
        // one platform would be invisible until somebody tried to share a fix between them.
        let encoded = encode_tile(&[0xAA, 0xBB]);

        assert_eq!(encoded[0], KIND_TILE);
        assert_eq!(&encoded[1..5], &2u32.to_be_bytes());
        assert_eq!(&encoded[5..], &[0xAA, 0xBB]);
        assert_eq!(HEADER_BYTES, 5);
    }

    #[test]
    fn a_listener_replaces_a_stale_socket_file() {
        // A service killed rather than stopped leaves the file behind, and bind() fails on an
        // existing path whether or not anything is listening.
        let directory = std::env::temp_dir().join(format!("kintsugi-ipc-test-{}", std::process::id()));
        std::fs::create_dir_all(&directory).unwrap();
        let path = directory.join("remote-control.sock");

        std::fs::write(&path, b"stale").unwrap();
        let listener = IpcListener::create(&path);

        assert!(listener.is_ok(), "{:?}", listener.err());
        drop(listener);

        // And it cleans up after itself, so the next start finds nothing.
        assert!(!path.exists());
        let _ = std::fs::remove_dir_all(&directory);
    }

    #[test]
    fn the_socket_is_created_world_writable_inside_a_traverse_only_directory() {
        use std::os::unix::fs::PermissionsExt;

        // 0666 is deliberate and only safe because of the peer check in `accept` — see the module
        // note. Pinned so a later "tightening" to 0600 does not silently make the per-user process
        // unable to connect at all.
        let directory = std::env::temp_dir().join(format!("kintsugi-ipc-mode-{}", std::process::id()));
        std::fs::create_dir_all(&directory).unwrap();
        let path = directory.join("remote-control.sock");

        let _listener = IpcListener::create(&path).unwrap();
        let mode = std::fs::metadata(&path).unwrap().permissions().mode() & 0o777;

        assert_eq!(mode, 0o666, "{mode:o}");
        let _ = std::fs::remove_dir_all(&directory);
    }
}
