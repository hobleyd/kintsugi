//! The local channel between the service and the tray process, which is the whole reason Windows
//! remote control is shaped differently from macOS.
//!
//! # Why this exists at all
//!
//! On macOS the per-user process holds this host's mutual-TLS identity, so it opens the WebSocket to
//! the server itself and remote control is one process end to end. On Windows the identity is
//! deliberately locked to SYSTEM and Administrators (see `config::identity_dir`) so the client
//! private key is not readable by whoever happens to be logged in — and the tray process therefore
//! makes no network call at all.
//!
//! But the *screen* is only reachable from the tray process: session 0 isolation means a service
//! cannot see, capture or type into a logged-in user's desktop, whatever privileges it holds. So
//! neither half can do this alone, and the two capabilities are on opposite sides of a boundary that
//! exists on purpose. This pipe is that boundary:
//!
//! - the **service** holds the WebSockets, because it has the identity;
//! - the **tray** captures the screen, posts the input and asks the user, because it has the desktop;
//! - the service relays bytes between the two, understanding only enough to know when consent was
//!   granted.
//!
//! The alternative — handing the certificate and key to the tray process so it could open the socket
//! the way macOS does — was rejected: it would put the fleet private key in the address space of a
//! process running as whoever is logged in, which is exactly what the identity directory's ACL
//! exists to prevent.
//!
//! # Why not the existing queue
//!
//! `queue.rs` already carries requests between these two processes, and remote control does not use
//! it. A queue entry is a file in a drop-box directory, polled every two seconds — right for "run
//! this patch", hopeless for fifteen screen frames a second. Splitting negotiation across the file
//! queue and streaming across a pipe would mean two mechanisms for one feature, so all of remote
//! control goes over the pipe.
//!
//! The queue's security property is preserved rather than weakened. **A pipe message never carries
//! anything executable** either: the service→tray direction carries a session request and the
//! viewer's input, and the tray→service direction carries an answer and JPEG bytes. Nothing on it
//! is a script, a path or a command.
//!
//! # No interactive user on either end any more
//!
//! This pipe used to run between the service and the *tray* process, which meant it had to be
//! reachable by whoever was logged in — and that carried a real attack: a local process could race
//! to answer a consent request and then feed the administrator a fabricated screen while swallowing
//! their keystrokes. The session helper being SYSTEM removes the whole category. Both ends are now
//! privileged, so the ACL grants Local System and Administrators and **nothing else**, and an
//! unprivileged process cannot open the pipe at all.
//!
//! What replaced the session check is stricter as well as simpler: the service launches the helper
//! itself, so it knows the process id it is expecting, and [`PipeListener::accept`] reports the
//! client's id for the caller to compare. Guessing is not an option and neither is racing — the pipe
//! is unreachable without SYSTEM or Administrators in the first place.


use anyhow::{anyhow, Context, Result};
use serde::{Deserialize, Serialize};

use crate::remote_protocol::ConsentOutcome;

/// The pipe both halves agree on. A fixed name, not per-session: exactly one console session exists
/// at a time, and it is the only one allowed to connect.
pub const PIPE_NAME: &str = r"\\.\pipe\kintsugi-agent-remote-control";

/// Message kinds on the wire. Two, because the media protocol's own text/binary distinction has to
/// survive the trip — the viewer relies on it (JSON control vs JPEG tile), and a pipe has no
/// framing of its own to carry it.
const KIND_JSON: u8 = 1;
const KIND_TILE: u8 = 2;

/// Frame header: kind byte plus a big-endian length.
const HEADER_BYTES: usize = 5;

/// The largest single message the pipe will carry. A full-screen JPEG at the quality the agent uses
/// is a few hundred kilobytes; this is generous next to that and bounds what a malformed length
/// field can make either side allocate.
const MAX_MESSAGE_BYTES: u32 = 8 * 1024 * 1024;

/// Everything the two halves say to each other.
///
/// One enum for both directions, with the direction noted per variant rather than split into two
/// types — the pipe is symmetric and a single `match` on the receiving side is easier to read than
/// two near-identical ones.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "type")]
pub enum IpcMessage {
    /// Service to tray: the server is asking for this host. Put the consent dialog up.
    #[serde(rename = "session-requested")]
    SessionRequested {
        #[serde(rename = "sessionId")]
        session_id: String,
        #[serde(rename = "requestedBy")]
        requested_by: String,
        #[serde(rename = "consentTimeoutSeconds")]
        consent_timeout_seconds: u64,
    },

    /// Service to tray: stop capturing. Sent when the administrator hangs up, when the server ends
    /// the session, and when the socket to the server dies.
    #[serde(rename = "session-ended")]
    SessionEnded {
        #[serde(rename = "sessionId")]
        session_id: String,
        reason: String,
    },

    /// Service to tray: one pointer, key or quality message, exactly as the viewer sent it. Passed
    /// through as an opaque string — the service has no reason to understand input, and parsing it
    /// would be a second place for the media protocol to drift.
    #[serde(rename = "viewer-input")]
    ViewerInput { json: String },

    /// Tray to service: what the person at the keyboard said.
    #[serde(rename = "consent")]
    Consent {
        #[serde(rename = "sessionId")]
        session_id: String,
        outcome: ConsentOutcome,
    },

    /// Tray to service: the display geometry, to be forwarded to the viewer as a text message.
    #[serde(rename = "display")]
    DisplayInfo { json: String },

    /// Tray to service: the person at the keyboard ended it from the tray menu, or capture failed.
    #[serde(rename = "ended-by-host")]
    EndedByHost {
        #[serde(rename = "sessionId")]
        session_id: String,
        reason: String,
    },
}

/// A whole message read off the pipe. Tiles are kept out of [`IpcMessage`] so a few hundred
/// kilobytes of JPEG never goes near a JSON encoder.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum IpcFrame {
    Json(IpcMessage),
    /// Tray to service: one JPEG tile, already framed for the media protocol by
    /// `remote_protocol::encode_tile`. Relayed to the viewer as a binary message, unexamined.
    Tile(Vec<u8>),
}

/// Encodes one message for the wire.
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
/// A byte-mode pipe delivers whatever happens to have arrived, so a read can hand back half a header
/// or three messages at once. Keeping the reassembly here — and pure — means the two very different
/// callers (a service relay loop and a tray session loop) share one implementation of the part that
/// is easy to get subtly wrong.
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
    /// is unusable and the connection should be dropped — a length field beyond
    /// [`MAX_MESSAGE_BYTES`] or a kind byte that is not ours means the two ends disagree about the
    /// format, and there is no resynchronising from that.
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

// =================================================================================================
// The platform half. Everything above is portable and tested; everything below is Win32.
// =================================================================================================

#[cfg(windows)]
mod platform {
    use std::ffi::c_void;
    use std::fs::{File, OpenOptions};
    use std::mem::ManuallyDrop;
    use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
    use std::ptr;

    use anyhow::{anyhow, Context, Result};
    use windows_sys::Win32::Foundation::{
        GetLastError, LocalFree, ERROR_PIPE_CONNECTED, HANDLE, HLOCAL, INVALID_HANDLE_VALUE,
    };
    use windows_sys::Win32::Security::Authorization::ConvertStringSecurityDescriptorToSecurityDescriptorW;
    use windows_sys::Win32::Security::{SECURITY_ATTRIBUTES, SECURITY_DESCRIPTOR};
    use windows_sys::Win32::Storage::FileSystem::{PIPE_ACCESS_DUPLEX, WRITE_DAC};
    use windows_sys::Win32::System::Pipes::{
        ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, GetNamedPipeClientProcessId,
        PeekNamedPipe, PIPE_READMODE_BYTE, PIPE_TYPE_BYTE, PIPE_WAIT,
    };

    use crate::win32::wide;

    use super::PIPE_NAME;

    /// 64 KB each way. A JPEG tile is usually well under this, and a bigger buffer only delays the
    /// point at which a stalled reader shows up as backpressure.
    const BUFFER_BYTES: u32 = 64 * 1024;

    /// The pipe's DACL, in SDDL.
    ///
    /// `SY` (Local System) and `BA` (Builtin Administrators) only. Both ends of this pipe are now
    /// SYSTEM — the service on one side and the session helper on the other — so there is no reason
    /// for anybody else to be able to open it, and an interactive user cannot.
    ///
    /// **`IU` used to be here and its removal is the security win of the whole helper change.** With
    /// the tray as the other end, this had to admit interactive users, which meant reasoning about a
    /// local process racing to answer a consent request. That is now impossible rather than merely
    /// checked for.
    const PIPE_SDDL: &str = "D:(A;;GA;;;SY)(A;;GA;;;BA)";

    /// The service's end: creates the pipe and waits for the console session's tray to connect.
    pub struct PipeListener {
        handle: OwnedHandle,
    }

    impl PipeListener {
        pub fn create() -> Result<Self> {
            let name = wide(PIPE_NAME);
            let mut descriptor: *mut SECURITY_DESCRIPTOR = ptr::null_mut();

            // SAFETY: `PIPE_SDDL` is a valid NUL-terminated SDDL string; the out-parameter is
            // checked below and freed by `Drop` on the returned listener's descriptor.
            let converted = unsafe {
                ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    wide(PIPE_SDDL).as_ptr(),
                    1, // SDDL_REVISION_1
                    &mut descriptor as *mut _ as *mut *mut c_void,
                    ptr::null_mut(),
                )
            };

            if converted == 0 || descriptor.is_null() {
                // SAFETY: no preconditions.
                return Err(anyhow!(
                    "could not build the remote control pipe's security descriptor (error {})",
                    unsafe { GetLastError() }
                ));
            }

            let mut attributes = SECURITY_ATTRIBUTES {
                nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
                lpSecurityDescriptor: descriptor as *mut c_void,
                bInheritHandle: 0,
            };

            // SAFETY: a documented call; `name` and `attributes` outlive it, and the result is
            // checked against INVALID_HANDLE_VALUE.
            let handle = unsafe {
                CreateNamedPipeW(
                    name.as_ptr(),
                    PIPE_ACCESS_DUPLEX | WRITE_DAC,
                    PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                    // One instance. There is one console session, and it is the only one allowed to
                    // connect — see the module note on what a hostile local process could do.
                    1,
                    BUFFER_BYTES,
                    BUFFER_BYTES,
                    0,
                    &mut attributes,
                )
            };

            // SAFETY: allocated by the conversion above and no longer referenced once the pipe
            // exists — CreateNamedPipeW copies the descriptor it is given.
            unsafe { LocalFree(descriptor as HLOCAL) };

            if handle == INVALID_HANDLE_VALUE || handle.is_null() {
                // SAFETY: no preconditions.
                return Err(anyhow!("could not create the remote control pipe (error {})", unsafe {
                    GetLastError()
                }));
            }

            // SAFETY: a valid handle this function now owns exclusively.
            Ok(Self { handle: unsafe { OwnedHandle::from_raw_handle(handle as *mut _) } })
        }

        /// Blocks until something connects, and reports its process id alongside the connection.
        ///
        /// The id is reported rather than judged here: only the caller knows which helper it
        /// launched, and comparing against that is a stronger check than anything this function
        /// could apply on its own. Combined with the ACL — which admits nothing below SYSTEM or
        /// Administrators — that leaves no way for an unexpected process to be on the other end.
        pub fn accept(&self) -> Result<(PipeConnection, u32)> {
            let raw = self.handle.as_raw_handle() as HANDLE;

            // SAFETY: a documented call on a pipe handle this struct owns. ERROR_PIPE_CONNECTED
            // means a client won the race between CreateNamedPipeW and this call, which is a
            // success rather than a failure.
            let connected = unsafe { ConnectNamedPipe(raw, ptr::null_mut()) };
            if connected == 0 {
                // SAFETY: no preconditions.
                let error = unsafe { GetLastError() };
                if error != ERROR_PIPE_CONNECTED {
                    return Err(anyhow!("could not accept a remote control pipe client (error {error})"));
                }
            }

            let pid = client_process_id(raw).unwrap_or(0);
            Ok((PipeConnection::adopt(raw), pid))
        }
    }

    /// The process id at the other end of `pipe`, straight from the kernel.
    ///
    /// Unforgeable by the peer, which is what makes it worth asking — the caller compares it against
    /// the helper it launched itself.
    fn client_process_id(pipe: HANDLE) -> Result<u32> {
        let mut process_id: u32 = 0;
        // SAFETY: documented; `process_id` is a valid out-pointer.
        if unsafe { GetNamedPipeClientProcessId(pipe, &mut process_id) } == 0 {
            // SAFETY: no preconditions.
            return Err(anyhow!("GetNamedPipeClientProcessId failed (error {})", unsafe { GetLastError() }));
        }

        Ok(process_id)
    }

    /// Which end of the pipe a [`PipeConnection`] is, which decides what its `Drop` must do.
    ///
    /// The two ends have genuinely different obligations and conflating them was a real bug: the
    /// listener owns its pipe *instance* handle and reuses it for the next client, so a connection
    /// on that side must disconnect without closing; the client opened its own handle and must close
    /// it, or the helper leaks one per session.
    enum PipeRole {
        /// The service's side. The handle belongs to `PipeListener`.
        Listener,
        /// The session helper's side. The handle belongs to this connection.
        Client,
    }

    /// One connected pipe, read and written from a single thread.
    ///
    /// Single-threaded on purpose. A non-overlapped pipe handle serialises I/O per handle, so a
    /// thread blocked in `ReadFile` can block another thread's `WriteFile` on the same handle —
    /// which for a duplex channel carrying frames one way and input the other is a deadlock waiting
    /// to happen. Both loops that use this instead poll with [`Self::read_available`], which peeks
    /// before it reads and so never blocks.
    pub struct PipeConnection {
        /// `ManuallyDrop` because whether this closes is decided by `role`, not by scope — see
        /// [`PipeRole`].
        file: ManuallyDrop<File>,
        role: PipeRole,
    }

    impl PipeConnection {
        fn adopt(handle: HANDLE) -> Self {
            // SAFETY: the caller has a connected pipe handle belonging to the listener. Wrapped so
            // it can be read and written through `File`; `Drop` disconnects without closing, since
            // the listener reuses this same handle for the next client.
            Self {
                file: ManuallyDrop::new(unsafe { File::from_raw_handle(handle as *mut _) }),
                role: PipeRole::Listener,
            }
        }

        /// The tray's end: connects to the pipe the service is listening on.
        pub fn connect() -> Result<Self> {
            let file = OpenOptions::new()
                .read(true)
                .write(true)
                .open(PIPE_NAME)
                .with_context(|| format!("could not connect to {PIPE_NAME}"))?;

            Ok(Self { file: ManuallyDrop::new(file), role: PipeRole::Client })
        }

        /// Whatever has arrived, or an empty vector if nothing has.
        ///
        /// Peeks first, which is what makes this non-blocking without `PIPE_NOWAIT` — a mode
        /// Microsoft documents as present only for compatibility and advises against.
        pub fn read_available(&mut self) -> Result<Vec<u8>> {
            let raw = self.file.as_raw_handle() as HANDLE;
            let mut available: u32 = 0;

            // SAFETY: documented; every out-pointer is either valid or null, which this call
            // permits.
            let peeked = unsafe {
                PeekNamedPipe(raw, ptr::null_mut(), 0, ptr::null_mut(), &mut available, ptr::null_mut())
            };

            if peeked == 0 {
                // SAFETY: no preconditions.
                return Err(anyhow!("the remote control pipe closed (error {})", unsafe { GetLastError() }));
            }

            if available == 0 {
                return Ok(Vec::new());
            }

            let mut buffer = vec![0u8; available as usize];
            std::io::Read::read_exact(&mut *self.file, &mut buffer)
                .context("could not read from the remote control pipe")?;
            Ok(buffer)
        }

        pub fn write_all(&mut self, bytes: &[u8]) -> Result<()> {
            std::io::Write::write_all(&mut *self.file, bytes)
                .context("could not write to the remote control pipe")?;
            std::io::Write::flush(&mut *self.file).context("could not flush the remote control pipe")
        }
    }

    impl Drop for PipeConnection {
        fn drop(&mut self) {
            match self.role {
                // **The disconnect is required, not tidiness.** A pipe instance stays in the
                // connected state after its client goes, and `ConnectNamedPipe` on a still-connected
                // instance does not wait for a new client — so without this the service would accept
                // the tray once and then never again, and a tray that restarted (a logout, a
                // self-update, a crash) would be locked out until the whole service was restarted.
                // The handle itself is the listener's and must survive.
                PipeRole::Listener => {
                    // SAFETY: a connected pipe handle the listener still owns.
                    unsafe { DisconnectNamedPipe(self.file.as_raw_handle() as HANDLE) };
                }

                // SAFETY: this end opened the handle, so this end closes it — via `File`'s own
                // `Drop`, which is the only thing `ManuallyDrop` was suppressing.
                PipeRole::Client => unsafe { ManuallyDrop::drop(&mut self.file) },
            }
        }
    }

}

#[cfg(windows)]
pub use platform::{PipeConnection, PipeListener};

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
        // A byte-mode pipe hands back whatever happened to arrive, so this is the ordinary case
        // rather than an edge one.
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
        // There is no resynchronising a byte stream whose framing is not understood: the length that
        // follows cannot be trusted either, so the connection has to go.
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
    fn consent_outcomes_cross_the_pipe_by_name() {
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
    fn the_pipe_name_is_local_and_namespaced_to_this_agent() {
        // Local (\\.\pipe), never a UNC name: a pipe reachable over the network would be a remote
        // control channel any machine could try to answer.
        assert!(PIPE_NAME.starts_with(r"\\.\pipe\"));
        assert!(PIPE_NAME.contains("kintsugi-agent"));
    }
}
