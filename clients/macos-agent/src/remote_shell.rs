//! Root shell sessions, and the drop-box that gets a request from the per-user process to root.
//!
//! # Why this exists at all on macOS
//!
//! On Linux and Windows the process holding this host's identity is already root (or SYSTEM), so a
//! shell session simply runs there. macOS is the odd one out for the same reason it is odd
//! everywhere else in this agent: remote control lives in the **per-user** process, because the
//! screen and keyboard belong to a logged-in GUI session that the root daemon does not have.
//!
//! A terminal needs no GUI session, and a shell that ran as the logged-in user would be a shell
//! inside somebody's own login session opened without asking them — a materially different thing
//! from the root shell the other two agents give, and a worse one. So the shell runs as root here
//! too, and this module is the handoff that makes that possible.
//!
//! # The shape, and why it needs no server change
//!
//! The two sockets of a remote session are independent: the standing *control* socket negotiates,
//! and a per-session *media* socket carries the bytes. The server pairs a media socket to a session
//! by `(serialNumber, sessionId)` and authenticates it by the client certificate nginx verified —
//! and the root daemon reads **the same identity directory** the per-user process does, so it
//! presents the same certificate. Nothing on the server can tell, or needs to.
//!
//! So the per-user process answers consent on the control socket it already holds, drops a request
//! here, and stops. launchd's `WatchPaths` starts `kintsugi-agent --remote-shell`, which opens the
//! media socket itself, runs a root PTY on it, and exits when the session ends. The control socket
//! is untouched throughout, so a session dropping never costs this host its reachability.
//!
//! # Its own queue and its own job, deliberately
//!
//! Not a fourth [`crate::queue::RequestKind`]. That queue is drained by the check-in daemon inside
//! the invocation that registers this host and runs its patches, and launchd will not run two
//! instances of one job — so a shell session held open for a support call would stall this host's
//! check-ins for its whole length. See `config::REMOTE_SHELL_QUEUE_DIR`.
//!
//! # One session at a time, and the retry that can fall through it
//!
//! launchd will not run two instances of this job either, so a request arriving *while* a session
//! is running is not served until that session ends — by which point it has very likely aged past
//! [`REQUEST_TIMEOUT`] and is discarded. The server's one-session-per-host rule covers the ordinary
//! case, so the way to reach this is narrow: a session ends server-side (a dropped tab, a viewer
//! reconnecting) and the administrator asks again before this daemon has finished winding the last
//! one down. The symptom is a session reported as "the other end never connected", which points
//! nowhere near launchd — so it is written down here rather than left to be rediscovered. Fixing it
//! means either a resident job (which is the Linux shape, and a much larger change) or a longer
//! timeout traded against how long an abandoned request may sit here waiting to open a root shell.
//!
//! # What a forged request can do
//!
//! The queue directory is `root:admin 0770`, like the main one, so a local administrator can write
//! a request naming any session id. That buys nothing: the server refuses a media socket for a
//! session it did not create for this serial, and for one it has not seen answered — so the only id
//! that is accepted is one an authenticated administrator has *already* opened a shell session for,
//! and the shell was going to be attached to it anyway. This is the main queue's own property
//! ("the worst a forged request does is start an already-approved upgrade early") in its narrowest
//! form, and it is why the request carries a session id and nothing else — never a command, never a
//! shell to run, never an address to connect to.

use std::fs;
use std::io::Read;
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use anyhow::{anyhow, bail, Context, Result};

use tungstenite::Message;

use crate::config::{self, Config};
use crate::identity::{self, AgentIdentity};
use crate::logging;
use crate::pty::{self, ProgramSpec, Pty, INITIAL_COLS, INITIAL_ROWS};
use crate::remote_control::{self, Socket};
use crate::remote_protocol::{
    decode_shell_input, encode_shell_output, parse_viewer_input, ShellInfo, ViewerInput,
};

/// The extension a request is written with. Part of the on-disk protocol between the two halves —
/// the plist watches the directory and this module dispatches on the name, so it is load-bearing
/// rather than cosmetic.
const REQUEST_EXTENSION: &str = "remote-shell.request";

/// The LaunchDaemon's own job description, compiled into the binary rather than read out of the
/// installation archive.
///
/// It has to be here because of *when* the job now gets installed. A self-update is performed by
/// the binary that is already running, so the release that first shipped this job could not install
/// it on any host that reached that release by self-updating — 0.9.4's `self_update` had never
/// heard of a third plist, and every Mac it updated arrived at a binary understanding
/// `--remote-shell` with no job to run it under. Those hosts have no archive to read, so the repair
/// (`install_job_if_absent`, run on every root check-in) has to carry the file it installs. The
/// packaged copy is the same bytes: `include_str!` reads the very file packaging/publish-release.sh
/// puts in the archive and packaging/install.sh installs, so the two can no longer disagree.
const LAUNCHD_JOB_PLIST: &str = include_str!("../packaging/au.com.sharpblue.kintsugiagent-remote-shell.plist");

/// How old a request may be and still be run.
///
/// Far shorter than `queue::REQUEST_TIMEOUT`, because the thing waiting is not a person watching a
/// progress window but a session the server is holding open: it gives up on an unpaired session
/// after `RemoteControlDefaults.PairingTimeout` (30s). Anything older than this has certainly been
/// abandoned, and running it would open a root shell nobody is at the other end of. Generous enough
/// to cover a Mac that was briefly too busy to service a `WatchPaths` trigger.
const REQUEST_TIMEOUT: Duration = Duration::from_secs(120);

/// Accounts whose "shell" exists but refuses to be one — see the note on the Unix agents'
/// `remote_control::NOT_A_SHELL`, which screens the same paths for the same reason: every one of
/// these is a perfectly real file, so a password entry naming one would be started and exit at once.
const NOT_A_SHELL: [&str; 5] = [
    "/usr/sbin/nologin",
    "/sbin/nologin",
    "/usr/bin/nologin",
    "/usr/bin/false",
    "/bin/false",
];

/// How much terminal output is carried in one frame. Same value and reasoning as the other two
/// agents': a burst of output arrives as a handful of frames rather than one large one, so it
/// cannot stall the socket the keystrokes travel back on.
const READ_BUFFER_BYTES: usize = 32 * 1024;

/// How long the relay waits before looking again when the terminal had nothing to say. This is the
/// round trip a person feels between pressing a key and seeing it echoed, which a terminal is
/// judged on far more sharply than a remote pointer is.
const POLL_INTERVAL: Duration = Duration::from_millis(10);

// =================================================================================================
// The root side: installing the job that serves a request.
// =================================================================================================

/// Creates the drop-box and installs the LaunchDaemon that drains it, if this host has not got
/// them. Root only, and called from every root check-in (`main::run_daemon`).
///
/// # Why a check-in and not the installer
///
/// `self_update` replaces the binary and never re-runs packaging/install.sh, so a host in the field
/// has no other repair path — the same reasoning as the Linux agent's
/// `config::repair_directory_modes`, and found the same way. It is worse than that here, though:
/// the self-update is performed by the *old* binary, so a job introduced in release N is installed
/// by nothing at all on a host that self-updates from N-1 into it. That is not hypothetical, it is
/// what shipped in 0.9.5 — see `LAUNCHD_JOB_PLIST`. A check-in is the one thing every Mac in the
/// fleet runs hourly as root under whatever binary it currently has.
///
/// # Nothing here is fatal
///
/// A host without this job still checks in, patches and answers screen sessions; refusing to check
/// in over a terminal it may never be asked for would be the worse trade. Failures are logged,
/// because the symptom otherwise is a session that never connects.
///
/// Installed only when absent, so an administrator's own edits to the plist are never clobbered —
/// which also means a change to the packaged job's *contents* does not reach a host that already
/// has one. Both of those are deliberate; a job that had to be rewritten would need a version
/// marker in it and a reason to be worth the risk of stamping on a local edit.
pub fn install_job_if_absent() {
    // The directory `WatchPaths` watches has to exist before the job is loaded, or nothing wakes it.
    // `root:admin 0770`, matching the main queue: the logged-in administrator's process drops a
    // request in, and only root reads one.
    let queue_dir = config::remote_shell_queue_dir();
    if !queue_dir.is_dir() {
        if let Err(err) = fs::create_dir_all(&queue_dir) {
            logging::warn(&format!("could not create {}: {err}", queue_dir.display()));
            return;
        }
        if let Err(err) = fs::set_permissions(&queue_dir, fs::Permissions::from_mode(0o770)) {
            logging::warn(&format!("could not set the mode on {}: {err}", queue_dir.display()));
        }
        chown_root_admin(&queue_dir);
        logging::info(&format!("created the remote shell queue directory at {}", queue_dir.display()));
    }

    let installed = config::remote_shell_plist_path();
    if installed.exists() {
        return;
    }

    if let Err(err) = fs::write(&installed, LAUNCHD_JOB_PLIST) {
        logging::warn(&format!("could not install {}: {err}", installed.display()));
        return;
    }
    let _ = fs::set_permissions(&installed, fs::Permissions::from_mode(0o644));

    logging::info(&format!("installed the remote shell job at {}", installed.display()));

    // `bootstrap` rather than `kickstart`: the job has never been loaded on this host, so there is
    // nothing to kick. It is `WatchPaths`-triggered and `RunAtLoad` is false, so this loads it and
    // it then sits idle until a request appears.
    match Command::new("launchctl").arg("bootstrap").arg("system").arg(&installed).output() {
        Ok(output) if output.status.success() => {}
        Ok(output) => logging::warn(&format!(
            "launchctl bootstrap system {} exited with {}: {}",
            installed.display(),
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        )),
        Err(err) => logging::warn(&format!("failed to run launchctl bootstrap: {err}")),
    }
}

/// `root:admin` on a path, the same ownership packaging/install.sh gives the two queue directories.
///
/// Shelled out to rather than done with `chown(2)`, because the group id for `admin` has to be
/// looked up and `chown` on the command line does that itself.
fn chown_root_admin(path: &Path) {
    match Command::new("/usr/sbin/chown").arg("root:admin").arg(path).output() {
        Ok(output) if output.status.success() => {}
        Ok(output) => logging::warn(&format!(
            "chown root:admin {} exited with {}: {}",
            path.display(),
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        )),
        Err(err) => logging::warn(&format!("failed to run chown on {}: {err}", path.display())),
    }
}

// =================================================================================================
// The per-user side: dropping a request.
// =================================================================================================

/// Asks root to open a shell for this session. Called by the per-user process, which has already
/// answered the server's consent question.
///
/// Returns as soon as the file is written — there is deliberately no result to wait for. The
/// per-user process cannot see the session's outcome anyway (it is on a socket the daemon owns),
/// and the administrator learns it from the server, which is the one end that sees both sides.
pub fn request(session_id: &str) -> Result<()> {
    let queue_dir = config::remote_shell_queue_dir();

    // Reported rather than created. This process is the logged-in user's, and the directory's
    // parent is root's, so creating it here cannot work — an earlier version tried, and on every
    // Mac that self-updated into remote shells (see `LAUNCHD_JOB_PLIST`) each terminal session died
    // with a bare "Permission denied" naming a path, which reads as an ownership mistake in the
    // directory rather than as the absence of the whole job. And creating it would not have helped
    // if it had worked: a directory nothing watches collects requests nobody reads. Its absence
    // means exactly one thing, so that is what this says. `install_job_if_absent` fixes it on the
    // next root check-in.
    if !queue_dir.is_dir() {
        bail!(
            "the remote shell LaunchDaemon is not installed on this host: there is no {}. \
             The root daemon installs it on its next check-in (within the hour), or \
             packaging/install.sh installs it now",
            queue_dir.display()
        );
    }

    let path = queue_dir.join(format!("{}-{}.{REQUEST_EXTENSION}", now_epoch(), std::process::id()));

    // Written to a temporary name and renamed into place, so `WatchPaths` cannot wake the daemon on
    // a file that is still being written — a rename within one directory is atomic.
    let staging = path.with_extension("partial");
    std::fs::write(&staging, session_id)
        .with_context(|| format!("could not write {}", staging.display()))?;
    std::fs::rename(&staging, &path)
        .with_context(|| format!("could not move {} into place", staging.display()))?;

    logging::info(&format!("asked the root daemon to open a shell for session {session_id}"));
    Ok(())
}

// =================================================================================================
// The root side: running the session.
// =================================================================================================

/// Runs whatever is waiting in the queue, then returns. One launchd invocation of
/// `kintsugi-agent --remote-shell`.
///
/// Requests are claimed by *removing* the file before the session starts, so an invocation that
/// dies mid-session cannot have the same session replayed by the next `WatchPaths` trigger.
pub fn run(config: &Config, serial_number: &str) -> Result<()> {
    let queue_dir = config::remote_shell_queue_dir();

    // The same identity directory the per-user process reads, which is the whole reason this works
    // without a server change: the daemon presents the certificate the server already knows this
    // host by, so a media socket from here is indistinguishable from one from there.
    let identity = identity::load(&config::identity_dir())
        .ok_or_else(|| anyhow!("this host has not enrolled an identity yet"))?;

    for path in pending_requests(&queue_dir) {
        let claimed = claim(&path);

        let Some(session_id) = claimed else {
            continue;
        };

        if is_stale(&path) {
            // The server has long since given up on an unpaired session, so opening a root shell
            // now would attach it to nothing.
            logging::warn(&format!("discarding a remote shell request for session {session_id}: it is too old to run"));
            continue;
        }

        if let Err(err) = run_session(config, serial_number, &identity, &session_id) {
            logging::warn(&format!("remote shell session {session_id} failed: {err:#}"));
        }
    }

    Ok(())
}

/// Every request file in the directory, oldest first — the file names begin with the epoch second
/// they were written in, so a lexical sort is a chronological one.
fn pending_requests(queue_dir: &Path) -> Vec<PathBuf> {
    let Ok(entries) = std::fs::read_dir(queue_dir) else {
        return Vec::new();
    };

    let mut paths: Vec<PathBuf> = entries
        .flatten()
        .map(|entry| entry.path())
        .filter(|path| {
            path.file_name()
                .and_then(|name| name.to_str())
                .is_some_and(|name| name.ends_with(REQUEST_EXTENSION))
        })
        .collect();

    paths.sort();
    paths
}

/// Reads a request and removes it in one step, so no other invocation can take the same one.
///
/// Read before the remove rather than after: the file is small and the read cannot fail once it has
/// succeeded, whereas removing first would lose the session id if the process died in between.
fn claim(path: &Path) -> Option<String> {
    let mut contents = String::new();
    let read = std::fs::File::open(path)
        .and_then(|mut file| file.read_to_string(&mut contents))
        .is_ok();

    let _ = std::fs::remove_file(path);

    if !read {
        return None;
    }

    let session_id = contents.trim().to_string();
    (!session_id.is_empty()).then_some(session_id)
}

/// Whether this request has been waiting longer than anybody is still listening for.
///
/// Read from the name rather than the file's own mtime, which is what the writer controls and what
/// a copy would not preserve. A name this cannot parse is treated as fresh: the alternative is
/// silently discarding a session because a file name surprised us.
fn is_stale(path: &Path) -> bool {
    let Some(written) = epoch_in_file_name(path) else {
        return false;
    };

    now_epoch().saturating_sub(written) > REQUEST_TIMEOUT.as_secs()
}

fn epoch_in_file_name(path: &Path) -> Option<u64> {
    path.file_name()?.to_str()?.split('-').next()?.parse().ok()
}

fn now_epoch() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs()
}

/// Opens the media socket for this session and relays a root shell over it until it ends.
fn run_session(config: &Config, serial_number: &str, identity: &AgentIdentity, session_id: &str) -> Result<()> {
    let (program, user) = shell_program()?;

    let mut terminal = Pty::spawn(&program, INITIAL_COLS, INITIAL_ROWS)
        .with_context(|| format!("could not start {} in a terminal", program.program.display()))?;

    // No connect retry and no consent flush here, unlike the screen session's own connect: the
    // per-user process answered on the control socket and flushed it *before* writing the request
    // this invocation was started by, so the grant is at the server by construction.
    let url = config.remote_control_url(serial_number, Some(session_id));
    let mut socket = remote_control::connect(&url, identity)?;
    remote_control::set_nonblocking(&socket)?;

    // Before the first output frame, so the viewer can say what it is attached to — the shell
    // analogue of the display geometry a screen session sends first, and the thing that tells an
    // administrator this is root rather than somebody's own account.
    let info = serde_json::to_string(&ShellInfo::new(program.program.display().to_string(), &user))
        .context("could not describe the shell")?;
    remote_control::write_queued(&mut socket, Message::text(info))
        .context("could not tell the viewer what shell this is")?;

    let started = std::time::Instant::now();
    logging::info(&format!(
        "remote shell session {session_id} started ({} as {user})",
        program.program.display()
    ));

    let reason = relay(&mut socket, &mut terminal);

    // Before the socket closes, so a shell still running a foreground job is hung up on rather than
    // left holding this Mac's resources. `Pty`'s own `Drop` is the backstop for the paths above
    // this one that return early.
    if let Err(err) = terminal.terminate() {
        logging::warn(&format!("could not end the terminal for remote shell session {session_id}: {err}"));
    }

    let _ = socket.close(None);

    // Nothing is reported to the server here, and nothing needs to be: this process holds no
    // control socket, and the relay ends the session the moment this media socket closes. The
    // administrator sees the reason from the server, which is the one end that watches both sides.
    logging::info(&format!(
        "remote shell session {session_id} ended after {}s: {reason}",
        started.elapsed().as_secs()
    ));

    Ok(())
}

/// Moves bytes between the terminal and the viewer until one of them stops, and says why.
///
/// The same loop the Linux and Windows agents run, in the process that holds the identity on each.
fn relay(socket: &mut Socket, terminal: &mut Pty) -> String {
    let mut buffer = vec![0u8; READ_BUFFER_BYTES];

    loop {
        match pump_input(socket, terminal) {
            Ok(true) => {}
            Ok(false) => return "the viewer disconnected".to_string(),
            Err(err) => return format!("{err:#}"),
        }

        // Read once per turn rather than draining: a `yes` loop would otherwise never let the input
        // half run again, and the administrator could not press Ctrl-C.
        let produced = match terminal.read_available(&mut buffer) {
            // The shell closed its end — somebody typed `exit`. The ordinary way a session
            // finishes, and not a fault.
            Ok(Some(0)) => return "the shell exited".to_string(),
            Ok(Some(read)) => {
                if let Err(err) =
                    remote_control::write_queued(socket, Message::Binary(encode_shell_output(&buffer[..read]).into()))
                {
                    return format!("{err:#}");
                }
                true
            }
            Ok(None) => false,
            Err(err) => return format!("reading from the terminal failed: {err}"),
        };

        if let Err(err) = remote_control::flush(socket) {
            return format!("{err:#}");
        }

        if !produced {
            // Checked only once the terminal has nothing left to say, so its last output is on its
            // way first. Needed as well as the EOF above: a shell that exits while a background
            // process still holds the slave open produces no EOF at all.
            match terminal.try_wait() {
                Ok(Some(_)) => return "the shell exited".to_string(),
                Ok(None) => std::thread::sleep(POLL_INTERVAL),
                Err(err) => return format!("could not check on the shell: {err}"),
            }
        }
    }
}

/// Reads and applies everything the viewer has sent. `Ok(false)` means it hung up.
///
/// This is the one place in the media protocol where a *binary* message travels from the browser to
/// the agent: keystrokes are raw bytes rather than JSON, because a terminal carries arbitrary bytes
/// and escaping each one would double the traffic on the half of the connection latency is measured
/// on.
fn pump_input(socket: &mut Socket, terminal: &mut Pty) -> Result<bool> {
    loop {
        match socket.read() {
            Ok(Message::Binary(bytes)) => {
                // Anything that is not shell input — a tile echoed back, a frame from a newer
                // viewer — is dropped rather than typed. `decode_shell_input` is what decides.
                if let Some(input) = decode_shell_input(&bytes) {
                    terminal.write_all(input).context("writing to the terminal failed")?;
                }
            }
            Ok(Message::Text(text)) => {
                if let Some(ViewerInput::Resize { cols, rows }) = parse_viewer_input(&text) {
                    // Failing to resize is not worth ending a session over: the shell keeps
                    // working, it just wraps at the wrong width.
                    if let Err(err) = terminal.resize(cols, rows) {
                        logging::warn(&format!("could not resize the terminal: {err}"));
                    }
                }
            }
            Ok(Message::Close(_)) => return Ok(false),
            Ok(_) => {}
            Err(err) if remote_control::is_would_block(&err) => return Ok(true),
            Err(tungstenite::Error::ConnectionClosed) | Err(tungstenite::Error::AlreadyClosed) => return Ok(false),
            Err(err) => return Err(anyhow!(err).context("reading from the remote shell session socket")),
        }
    }
}

/// Root's own login shell, home directory, and the smallest environment a shell needs to behave
/// like one opened by hand.
///
/// This process is root — it is a LaunchDaemon — so `getpwuid(getuid())` is root's entry, which is
/// exactly the same call the Linux agent's resident root unit makes and gets the same answer from.
/// Reading the password database rather than this process's environment is not incidental: launchd
/// starts a daemon with almost nothing set, so `$SHELL` and `$HOME` are routinely absent here.
fn shell_program() -> Result<(ProgramSpec, String)> {
    let (user, home, shell) = passwd_entry().context("could not read root's password database entry")?;

    // A password entry naming a shell that is not installed is rare but survivable, and so is one
    // naming a refusal like `/usr/bin/false` — see NOT_A_SHELL, which is screened by name because
    // such a path is a perfectly real file and would otherwise be started and exit at once.
    let program = pty::first_existing(&[shell.as_str()])
        .filter(|chosen| !NOT_A_SHELL.contains(&chosen.to_string_lossy().as_ref()))
        .or_else(|| pty::first_existing(&["/bin/zsh", "/bin/bash", "/bin/sh"]))
        .ok_or_else(|| anyhow!("no usable login shell for {user} (the password database names {shell})"))?;

    // A home directory that does not exist would make the shell fail to start at all, which reads
    // as "remote shell is broken on this host" rather than as the misconfiguration it is.
    let cwd = PathBuf::from(&home);
    let cwd = if cwd.is_dir() { cwd } else { PathBuf::from("/") };

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
                ("PATH".to_string(), "/usr/bin:/bin:/usr/sbin:/sbin".to_string()),
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

#[cfg(test)]
mod tests {
    use super::*;

    fn write(dir: &Path, name: &str, body: &str) -> PathBuf {
        let path = dir.join(name);
        std::fs::write(&path, body).expect("write");
        path
    }

    /// CLAUDE.md calls the remote-shell handoff "four names that nothing checks agree": the request
    /// suffix, the queue directory, the job label and what the plist says about both. A test pinned
    /// the suffix and nothing pinned the rest, so this pins the rest — the plist is compiled in
    /// (see `LAUNCHD_JOB_PLIST`), which is what makes it checkable at all. Get one of these wrong
    /// and the per-user process writes a request nothing ever reads: the session is reported as
    /// never connecting, and neither log says why.
    #[test]
    fn the_packaged_job_watches_the_directory_requests_are_written_to() {
        assert!(
            LAUNCHD_JOB_PLIST.contains(&format!("<string>{}</string>", config::remote_shell_queue_dir().display())),
            "the plist's WatchPaths does not name config::remote_shell_queue_dir()"
        );
        assert!(
            LAUNCHD_JOB_PLIST.contains(&format!("<string>{}</string>", config::REMOTE_SHELL_LAUNCHD_LABEL)),
            "the plist's Label does not match config::REMOTE_SHELL_LAUNCHD_LABEL"
        );
        assert!(
            LAUNCHD_JOB_PLIST.contains(&format!("<string>{}</string>", config::installed_binary_path().display())),
            "the plist runs a binary this agent does not install"
        );
        // The arm in `main` that this module is reached through. Renaming one and not the other
        // starts the daemon, which then runs an ordinary check-in and never opens the session.
        assert!(
            LAUNCHD_JOB_PLIST.contains("<string>--remote-shell</string>"),
            "the plist does not pass --remote-shell"
        );
    }

    #[test]
    fn finds_only_request_files_and_returns_them_oldest_first() {
        let dir = std::env::temp_dir().join(format!("kintsugi-shell-{}", std::process::id()));
        std::fs::create_dir_all(&dir).expect("create");

        write(&dir, &format!("1700000200-9.{REQUEST_EXTENSION}"), "later");
        write(&dir, &format!("1700000100-9.{REQUEST_EXTENSION}"), "earlier");
        // Neither of these is a request: the half-written staging file is exactly what the atomic
        // rename in `request` exists to keep the daemon away from.
        write(&dir, "1700000150-9.remote-shell.partial", "half written");
        write(&dir, "notes.txt", "");

        let found = pending_requests(&dir);
        let names: Vec<String> = found
            .iter()
            .map(|path| path.file_name().unwrap().to_string_lossy().into_owned())
            .collect();

        assert_eq!(
            names,
            vec![
                format!("1700000100-9.{REQUEST_EXTENSION}"),
                format!("1700000200-9.{REQUEST_EXTENSION}")
            ]
        );

        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn claiming_reads_the_session_id_and_removes_the_file() {
        // Removal is what stops the next WatchPaths trigger replaying a session that already ran.
        let dir = std::env::temp_dir().join(format!("kintsugi-shell-claim-{}", std::process::id()));
        std::fs::create_dir_all(&dir).expect("create");

        let path = write(&dir, &format!("1700000100-9.{REQUEST_EXTENSION}"), "  abc-123\n");

        assert_eq!(claim(&path), Some("abc-123".to_string()));
        assert!(!path.exists(), "the request was not claimed");
        // And a second attempt gets nothing rather than the same session again.
        assert_eq!(claim(&path), None);

        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn an_empty_request_is_not_a_session() {
        let dir = std::env::temp_dir().join(format!("kintsugi-shell-empty-{}", std::process::id()));
        std::fs::create_dir_all(&dir).expect("create");

        let path = write(&dir, &format!("1700000100-9.{REQUEST_EXTENSION}"), "   \n");
        assert_eq!(claim(&path), None);
        assert!(!path.exists(), "an unusable request must still be cleared away");

        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn a_request_older_than_the_timeout_is_stale_and_a_fresh_one_is_not() {
        let fresh = PathBuf::from(format!("/tmp/{}-9.{REQUEST_EXTENSION}", now_epoch()));
        assert!(!is_stale(&fresh));

        let old = PathBuf::from(format!(
            "/tmp/{}-9.{REQUEST_EXTENSION}",
            now_epoch() - REQUEST_TIMEOUT.as_secs() - 1
        ));
        assert!(is_stale(&old));
    }

    #[test]
    fn an_unparseable_name_is_treated_as_fresh_rather_than_discarded() {
        // Discarding a session because a file name surprised us is the worse failure: the
        // administrator sees a session that never connects, with nothing to say why.
        assert!(!is_stale(&PathBuf::from(format!("/tmp/whenever.{REQUEST_EXTENSION}"))));
    }

    #[test]
    fn the_request_extension_is_the_one_the_plist_watches_for() {
        // packaging/au.com.sharpblue.kintsugiagent-remote-shell.plist watches the directory, and
        // `run` dispatches on this suffix. A rename on one side alone is a session that is never
        // started, with nothing in either log to say so.
        assert_eq!(REQUEST_EXTENSION, "remote-shell.request");
    }
}
