use std::fs;
use std::io::{Read, Write};
use std::os::unix::fs::{MetadataExt, OpenOptionsExt, PermissionsExt};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

use crate::forced_patch_run::ForcedPatchRun;

/// The privilege handoff between this agent's two halves, and the reason it exists at all.
///
/// The macOS agent's handoff is partial: its per-user process holds this host's identity and runs
/// Homebrew upgrades itself (Homebrew refuses to run as root), handing the daemon only what needs
/// root — an OS update, and an AI-researched script against a root-owned `/Applications` bundle
/// (see its `upgrade::runs_as_root`). On Linux, as on Windows, nothing of that is possible
/// unprivileged:
///
/// - **Running an upgrade** means `apt-get`/`dnf upgrade`, `flatpak update --system`, or
///   `snap refresh`, every one of which writes outside any user's home directory and every one of
///   which requires root. There is no per-user equivalent that would work instead. (macOS gets to
///   avoid this precisely because its one package manager, Homebrew, *refuses* to run as root and
///   installs into a user-writable prefix — the opposite constraint, leading to the opposite
///   design.)
/// - **Asking what's pending** needs this host's mutual-TLS identity, and that identity is
///   deliberately root-only here (see `config::IDENTITY_DIR`) so the client private key isn't
///   readable by whoever happens to be logged in. On a shared Linux host that matters more than it
///   does on a single-admin Mac.
///
/// So on Linux the per-user process performs no privileged action and makes no network call at
/// all: it decides *when* to patch (policy, schedule, the confirm/delay dialog, progress) and asks
/// the root service to do each step. The security property the macOS queue was built around is
/// preserved exactly, and strengthened the same way the Windows one strengthens it: **a request
/// never carries anything executable.** An app-patch request names an application and nothing
/// else; the service independently fetches that application's upgrade path from the server and
/// verifies its signature before running anything (see `upgrade::patch_one`). A malicious or
/// corrupted request file can, at worst, cause an already-approved upgrade to run early — never
/// arbitrary code as root.
///
/// The directory is a drop-box: `root:root 1733`, so any logged-in user may write a request into
/// it, nobody but root may list or read it, and the sticky bit stops one user deleting another's.
/// That is the Linux spelling of the macOS queue's `root:admin 0770` and the Windows queue's
/// `BUILTIN\Users` ACL — and it needs no group, which matters because the "local administrators"
/// group is `sudo` on Debian, `wheel` on Red Hat, and neither on plenty of others. See
/// packaging/install.sh.
///
/// "Any logged-in user" is enforced, not assumed. Every name in that directory was chosen by an
/// unprivileged process, so the root side opens each entry without following a symlink and acts on
/// a request only when the file is owned by a uid that has a login session on this host
/// (`owner_may_use_the_queue`); a result is written fresh under a name root has just unlinked
/// (`write_result`), and the per-user side believes a result only if the queue directory's owner
/// wrote it (`submit`). A heartbeat counts only if it is owned by the uid its name carries, that uid
/// is logged in, and its timestamp is not further ahead of the clock than `HEARTBEAT_MAX_AGE`
/// (`live_ui_agent_with`). None of this leans on the kernel's `fs.protected_symlinks`, which on a
/// default kernel happens to refuse root a symlink follow in a sticky world-writable directory —
/// that is a sysctl, not a property of this design.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RequestKind {
    /// "What is there to patch right now?" — answered from the server, with this host's identity.
    Plan,
    /// "Run the (already server-signed) upgrade for this one application." Body: the application
    /// name, exactly as reported in the inventory.
    AppPatch,
    /// "Install pending OS updates." No body — the service always runs the same fixed install, so
    /// this can never carry instructions of its own.
    OsUpdate,
    /// "Check in with the server now" — the menu's "Check In Now", see
    /// `checkin_schedule::request_now`. No body — the queue service runs the same check-in the
    /// timer runs on the hour (`main::check_in`), just now.
    CheckIn,
    /// "Has an administrator forced a patch run against this host?" — asked every
    /// `forced_patch_run::POLL_INTERVAL` by the per-user process, which holds no identity and so
    /// cannot ask the server itself. No body, and nothing a forged one could influence: the service
    /// asks the server about *this* host and no other, the same as for a `Plan`.
    ///
    /// Note what a forged request can still do, since it is the one thing here that is not merely an
    /// early read: the server marks the instructions collected as it answers, so a request the real
    /// per-user process never sees consumes them. That costs a local user nothing they did not
    /// already have — the per-user process is theirs to kill, which hands the schedule back to the
    /// root service — but it is worth knowing before this kind is given a body or a second reader.
    ForcedPatchRuns,
}

impl RequestKind {
    /// The file extension a request of this kind is written with. Part of the on-disk protocol
    /// between the two halves: the service dispatches on it (see `process_queue`), so these
    /// strings are load-bearing rather than cosmetic.
    fn extension(self) -> &'static str {
        match self {
            RequestKind::Plan => "plan.request",
            RequestKind::AppPatch => "app-patch.request",
            RequestKind::OsUpdate => "os-update.request",
            RequestKind::CheckIn => "check-in.request",
            RequestKind::ForcedPatchRuns => "forced-patch-runs.request",
        }
    }

    fn from_file_name(file_name: &str) -> Option<Self> {
        for kind in [RequestKind::Plan, RequestKind::AppPatch, RequestKind::OsUpdate, RequestKind::CheckIn, RequestKind::ForcedPatchRuns] {
            if file_name.ends_with(kind.extension()) {
                return Some(kind);
            }
        }
        None
    }
}

/// How long after it was written a request is still considered live. Anything older is discarded
/// unread.
///
/// The Windows service sweeps the queue once at its own startup instead, which it can do because
/// it is resident. The Linux root half isn't — it's a systemd oneshot woken by a `.path` unit
/// (see packaging/kintsugi-agent-queue.path), so it has no "startup" moment that happens exactly
/// once per boot. Judging by age instead is both simpler and stricter: a request that has sat
/// unanswered for this long has an owner that is gone (rebooted, logged out, killed), and acting
/// on it would start an unannounced patch cycle with no progress window and no warning — worse
/// than not patching at all. A live request is picked up within a second or so of being written,
/// so the margin here is enormous.
const MAX_REQUEST_AGE: Duration = Duration::from_secs(5 * 60);

/// What the service writes back for a completed request. `data` carries a `Plan`'s answer and is
/// absent for the two action kinds, which have nothing to report beyond success and output.
#[derive(Debug, Serialize, Deserialize)]
pub struct RequestResult {
    pub success: bool,
    pub output: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub data: Option<Plan>,
    /// A [`RequestKind::ForcedPatchRuns`] answer, empty for every other kind.
    ///
    /// A second field rather than one polymorphic payload, because the two answers are different
    /// shapes and a reader taking the wrong one would be a silent mismatch rather than a parse
    /// error. Both are defaulted on read, so a per-user process and a root service at different
    /// versions — the normal state of affairs for the minutes around a self-update — still
    /// understand each other.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub forced: Vec<ForcedPatchRun>,
}

/// One application the server says is patchable on this host, reduced to just what the per-user
/// process needs to show progress. Deliberately not the full `UpgradeStatus`: that process never
/// sees a script, a command, or a signature, because it never runs one.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PlannedApp {
    pub application_name: String,
    pub latest_version: Option<String>,
}

/// The answer to a [`RequestKind::Plan`] request — the Linux equivalent of what
/// `patch_cycle::plan` computes inline on macOS.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct Plan {
    pub apps: Vec<PlannedApp>,
    pub os_update_available: bool,
}

impl Plan {
    /// The names the confirmation dialog lists, in the order they will be patched — see
    /// `dialogs::confirmation_message`.
    pub fn app_names(&self) -> Vec<String> {
        self.apps.iter().map(|app| app.application_name.clone()).collect()
    }

    pub fn total(&self) -> usize {
        self.apps.len() + usize::from(self.os_update_available)
    }

    pub fn is_empty(&self) -> bool {
        self.total() == 0
    }

    /// Narrows this plan to the applications an administrator actually forced, and drops the OS
    /// update with it.
    ///
    /// Both halves matter. "Force this application" names one row on one screen, so a forced run
    /// that also installed every other pending patch — and an OS update, which on most
    /// distributions pulls in a kernel and wants a reboot — would do enormously more than was
    /// asked, in the one situation where the person asking is least able to absorb the surprise.
    /// And the names are matched case-insensitively because that is how applications are matched
    /// everywhere else in this agent and on the server.
    ///
    /// Lives on `Plan` here rather than in `patch_cycle` (where the macOS agent keeps it, on its
    /// own `PendingWork`) only because `Plan` is this agent's version of that type and it lives in
    /// this file. Kept identical in behaviour to the other two.
    pub fn narrowed_to(self, application_names: &[String]) -> Plan {
        let apps = self
            .apps
            .into_iter()
            .filter(|app| application_names.iter().any(|name| name.eq_ignore_ascii_case(&app.application_name)))
            .collect();

        Plan { apps, os_update_available: false }
    }
}

fn now_epoch() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs()
}

fn result_path_for(request_path: &Path) -> PathBuf {
    let mut path = request_path.as_os_str().to_os_string();
    path.push(".result.json");
    PathBuf::from(path)
}

/// Opens one entry in the queue directory the way both sides must: without following a symlink,
/// without blocking on a FIFO, and refusing anything that is not a regular file. Returns the file
/// and the metadata of the thing actually opened — `fstat`, not `stat` — so every decision made
/// downstream (owner, age) is about the bytes that will be read, not about whatever the path
/// pointed at a moment earlier.
///
/// Before this, a request was read with `read_to_string` and a result written with `fs::write`,
/// both of which follow a symlink — so a name in the drop-box could point root at any file it can
/// read, or at any file it can write. A default Linux kernel's `fs.protected_symlinks` refuses
/// root that follow in a sticky world-writable directory, which is the only reason that was never
/// more than a finding; a kernel tuned otherwise loses the backstop, and this does not need it.
fn open_queue_entry(path: &Path) -> std::io::Result<(fs::File, fs::Metadata)> {
    let file = fs::OpenOptions::new()
        .read(true)
        // O_NONBLOCK so that a FIFO planted under a request's name is opened and then refused by
        // the file-type check below, rather than blocking the root service forever.
        .custom_flags(libc::O_NOFOLLOW | libc::O_NONBLOCK | libc::O_CLOEXEC)
        .open(path)?;
    let metadata = file.metadata()?;
    if !metadata.file_type().is_file() {
        return Err(std::io::Error::new(std::io::ErrorKind::InvalidInput, "not a regular file"));
    }
    Ok((file, metadata))
}

/// Whether `uid` currently has a login session on this host — which is what makes it a user whose
/// per-user process could legitimately have written a request or a heartbeat.
///
/// systemd-logind creates `/run/user/<uid>` when a user's first session opens and removes it when
/// the last closes (or keeps it, with lingering enabled — and a lingering user's `systemd --user`
/// is exactly one that may be running the per-user unit). That same `systemd --user` is what starts
/// the per-user process, so anything it writes is from a uid this returns true for. What it rules
/// out is everything else: a daemon account, or a uid that exists nowhere at all — a forged
/// `ui-424242.heartbeat` held a host's patching schedule indefinitely before this check existed.
fn uid_has_login_session(uid: u32) -> bool {
    Path::new("/run/user").join(uid.to_string()).is_dir()
}

/// Which uids the privileged side acts for. Injected rather than called directly so the tests — run
/// as whoever runs them, with or without a session, root in a container — can say what counts.
type SessionCheck<'a> = &'a dyn Fn(u32) -> bool;

fn owner_may_use_the_queue(metadata: &fs::Metadata, has_session: SessionCheck) -> Result<(), String> {
    let uid = metadata.uid();
    if has_session(uid) {
        Ok(())
    } else {
        Err(format!("owned by uid {uid}, which has no login session on this host"))
    }
}

/// The uid that owns the queue directory: root on an installed host, whoever runs the tests under a
/// scratch directory. A result is believed only when its owner is this — the service is the only
/// thing that writes results, and it is the only thing that could own the directory they go in.
fn queue_owner_uid(queue_dir: &Path) -> Option<u32> {
    fs::metadata(queue_dir).ok().map(|metadata| metadata.uid())
}

/// Writes a result where nothing but this process can have put a file first.
///
/// Whatever already sits at `result_path` was not written by this process — results are removed
/// by their reader, and this request is only now being answered — so it is a leftover or a plant: a
/// file some other user created under a predicted name to be handed to the requester as the answer.
/// The sticky bit stops users unlinking each other's files; it does not stop root. So the entry is
/// unlinked and the result created fresh, `O_EXCL` so a name that reappears in between is an error
/// and `O_NOFOLLOW` so it is never written through.
fn write_result(result_path: &Path, json: &str) -> std::io::Result<()> {
    match fs::remove_file(result_path) {
        Ok(()) => {}
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => {}
        Err(err) => return Err(err),
    }

    let mut file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .mode(0o644)
        .custom_flags(libc::O_NOFOLLOW | libc::O_CLOEXEC)
        .open(result_path)?;
    file.write_all(json.as_bytes())?;
    // World-readable on purpose, and this is the one place it matters: the queue directory itself
    // is unreadable to anyone but root, so a result file is only ever reachable by a process that
    // already knows the exact path — which is to say, the one that wrote the matching request. It
    // carries no secret either way; the whole point of this design is that nothing executable or
    // confidential crosses it. Set on the open descriptor rather than by path, so the umask cannot
    // have narrowed it and the path cannot have changed under us.
    file.set_permissions(fs::Permissions::from_mode(0o644))
}

/// Drops a request into the queue and returns its path. The file name carries the process id and a
/// monotonic counter alongside the timestamp so two requests made inside the same second — which a
/// patch cycle does constantly, one per application — can't collide on a name and read each
/// other's results.
fn write_request(queue_dir: &Path, kind: RequestKind, body: &str) -> Result<PathBuf> {
    use std::sync::atomic::{AtomicU64, Ordering};
    static COUNTER: AtomicU64 = AtomicU64::new(0);

    // Not `create_dir_all`: the installer creates this directory with the exact ownership and mode
    // the whole design rests on (`root:root 1733`), and the process calling this is unprivileged,
    // so a directory created here would be the user's own — a queue root could not safely act on.
    // Failing loudly instead is the correct outcome.
    if !queue_dir.is_dir() {
        // "Not reachable", not "not there": this process cannot list the state directory the queue
        // sits in, so a parent missing its execute bit fails `is_dir` exactly as an absent
        // directory does. 0.5.0 shipped that parent at 0700 and this message blamed the service
        // for not being installed on hosts where it was installed and checking in fine — see
        // `config::repair_directory_modes` and packaging/install.sh.
        anyhow::bail!(
            "the request queue directory {} is not reachable — is the kintsugi-agent service installed, and can this user traverse its parent?",
            queue_dir.display()
        );
    }

    let sequence = COUNTER.fetch_add(1, Ordering::Relaxed);
    let request_path = queue_dir.join(format!("{}-{}-{sequence}.{}", now_epoch(), std::process::id(), kind.extension()));
    fs::write(&request_path, body).context("could not write the queue request")?;
    Ok(request_path)
}

/// Submits a request and blocks (polling) until the service writes a result, or `timeout` elapses.
///
/// Polling rather than an inotify watch on this side on purpose: the per-user process is already
/// blocked on this one step and has nothing else to do, and a 1-second poll of a single known path
/// costs nothing next to the install it's waiting for. The *service* side is the one that can't
/// afford to poll, and doesn't — systemd's `.path` unit wakes it via inotify instead.
pub fn submit(queue_dir: &Path, kind: RequestKind, body: &str, timeout: Duration) -> Result<RequestResult> {
    let request_path = write_request(queue_dir, kind, body)?;
    let result_path = result_path_for(&request_path);

    let started = Instant::now();
    let service_uid = queue_owner_uid(queue_dir);
    loop {
        // Only the service writes results, and it owns the queue directory. A result owned by anyone
        // else was planted under this predicted name by another user, to be taken for the answer;
        // it is ignored (this process could not remove another user's file from a sticky directory
        // anyway) until the service unlinks it and writes the real one — see `write_result`.
        if let Ok((mut file, metadata)) = open_queue_entry(&result_path) {
            if Some(metadata.uid()) == service_uid {
                let mut contents = String::new();
                file.read_to_string(&mut contents).context("could not read the queue result written by the service")?;
                drop(file);
                let _ = fs::remove_file(&result_path);
                return serde_json::from_str(&contents).context("could not parse the queue result written by the service");
            }
        }

        if started.elapsed() >= timeout {
            // Removed so an abandoned request isn't executed long after the per-user process
            // stopped caring about it — a patch starting with no progress window and no warning is
            // worse than one that didn't start. `MAX_REQUEST_AGE` is the backstop for the case
            // where even this doesn't happen (the process was killed outright).
            let _ = fs::remove_file(&request_path);
            anyhow::bail!("timed out waiting for the kintsugi-agent service to answer a {kind:?} request");
        }

        std::thread::sleep(Duration::from_secs(1));
    }
}

/// What the service does with one request. Implemented in `main` (which owns the HTTP client and
/// this host's identity); kept as a trait so `process_queue`'s dispatch, ordering and staleness
/// logic can be tested without any of that.
pub trait RequestHandler {
    fn plan(&mut self) -> Result<Plan>;
    /// Answers a [`RequestKind::ForcedPatchRuns`] — see `forced_patch_run::collect`, which is what
    /// the root service's implementation calls.
    fn forced_patch_runs(&mut self) -> Result<Vec<ForcedPatchRun>>;
    fn patch_application(&mut self, application_name: &str) -> Result<()>;
    fn install_os_updates(&mut self) -> Result<()>;
    /// Answers a [`RequestKind::CheckIn`]. Returns the message the per-user process shows.
    fn check_in(&mut self) -> Result<String>;
}

/// The service's half of the handoff: processes every pending request found, oldest first, so a
/// request dropped while the service was already mid-run isn't silently skipped.
///
/// Runs to completion for each request before moving on, deliberately: two `apt-get` invocations
/// at once would deadlock on the dpkg lock, and the per-user process asks for one application at a
/// time anyway.
pub fn process_queue(queue_dir: &Path, handler: &mut impl RequestHandler) {
    process_queue_with(queue_dir, handler, SystemTime::now(), &uid_has_login_session)
}

/// The testable half of [`process_queue`] — `now` is a parameter so the staleness rule can be
/// exercised without a test that has to sleep for minutes, and every uid is accepted, since the
/// tests run as whoever runs them.
#[cfg(test)]
fn process_queue_at(queue_dir: &Path, handler: &mut impl RequestHandler, now: SystemTime) {
    process_queue_with(queue_dir, handler, now, &|_| true)
}

fn process_queue_with(queue_dir: &Path, handler: &mut impl RequestHandler, now: SystemTime, has_session: SessionCheck) {
    let Ok(entries) = fs::read_dir(queue_dir) else {
        return;
    };

    let mut requests: Vec<PathBuf> = entries
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| {
            path.file_name()
                .and_then(|name| name.to_str())
                .is_some_and(|name| RequestKind::from_file_name(name).is_some())
        })
        .collect();
    // Lexicographic order is chronological here because every name starts with a fixed-width epoch
    // — good until the year 2286, by which point the sort key gains a digit.
    requests.sort();

    for request_path in requests {
        let Some(kind) = request_path
            .file_name()
            .and_then(|name| name.to_str())
            .and_then(RequestKind::from_file_name)
        else {
            continue;
        };

        // Opened before anything is decided about it, and everything below — owner, age, body — is
        // read from the opened file rather than from the path, which another user chose.
        let (mut file, metadata) = match open_queue_entry(&request_path) {
            Ok(opened) => opened,
            Err(err) => {
                crate::logging::warn(&format!(
                    "discarding a {kind:?} request that is not a regular file ({err}) without acting on it: {}",
                    request_path.display()
                ));
                discard(&request_path);
                continue;
            }
        };

        if let Err(reason) = owner_may_use_the_queue(&metadata, has_session) {
            crate::logging::warn(&format!("discarding a {kind:?} request {reason} without acting on it: {}", request_path.display()));
            discard(&request_path);
            continue;
        }

        if is_stale(&metadata, now) {
            crate::logging::warn(&format!(
                "discarding a {kind:?} request older than {} seconds without acting on it: {}",
                MAX_REQUEST_AGE.as_secs(),
                request_path.display()
            ));
            discard(&request_path);
            continue;
        }

        crate::logging::info(&format!("processing {kind:?} request: {}", request_path.display()));

        let mut body = String::new();
        if file.read_to_string(&mut body).is_err() {
            body.clear();
        }
        drop(file);
        let result = run_request(kind, body.trim(), handler);

        crate::logging::info(&format!(
            "{kind:?} request finished: success={} {}",
            result.success,
            result.output.trim()
        ));

        // The request is removed first, and the result written second: the per-user process waits
        // on the result file, so writing it while the request still existed would leave a window
        // where a crash here re-runs a request that has already been answered.
        let _ = fs::remove_file(&request_path);
        if let Ok(json) = serde_json::to_string(&result) {
            if let Err(err) = write_result(&result_path_for(&request_path), &json) {
                crate::logging::warn(&format!("could not write the queue result: {err}"));
            }
        }
    }
}

/// Removes a request that will not be acted on, and anything already sitting where its result
/// would go — the per-user process waits on that path, and a leftover there would be read as the
/// answer to the next request that happens to reuse the name.
fn discard(request_path: &Path) {
    let _ = fs::remove_file(request_path);
    let _ = fs::remove_file(result_path_for(request_path));
}

/// Whether a request has been sitting unanswered long enough that its owner must be gone — see
/// [`MAX_REQUEST_AGE`]. An unreadable or future-dated modification time counts as *not* stale, so
/// a clock that has just jumped backwards can't silently swallow live requests. Judged from the
/// metadata of the opened file, never from the path.
fn is_stale(metadata: &fs::Metadata, now: SystemTime) -> bool {
    metadata
        .modified()
        .ok()
        .and_then(|modified| now.duration_since(modified).ok())
        .is_some_and(|age| age > MAX_REQUEST_AGE)
}

fn run_request(kind: RequestKind, body: &str, handler: &mut impl RequestHandler) -> RequestResult {
    match kind {
        RequestKind::Plan => match handler.plan() {
            Ok(plan) => RequestResult {
                success: true,
                output: format!("{} application(s) pending, os_update_available={}", plan.apps.len(), plan.os_update_available),
                data: Some(plan),
                forced: Vec::new(),
            },
            Err(err) => RequestResult { success: false, output: format!("{err:#}"), data: None, forced: Vec::new() },
        },
        RequestKind::AppPatch => {
            if body.is_empty() {
                return RequestResult { success: false, output: "no application name in the request".to_string(), data: None, forced: Vec::new() };
            }
            match handler.patch_application(body) {
                Ok(()) => RequestResult { success: true, output: format!("patched {body}"), data: None, forced: Vec::new() },
                Err(err) => RequestResult { success: false, output: format!("{err:#}"), data: None, forced: Vec::new() },
            }
        }
        RequestKind::OsUpdate => match handler.install_os_updates() {
            Ok(()) => RequestResult { success: true, output: "installed pending OS updates".to_string(), data: None, forced: Vec::new() },
            Err(err) => RequestResult { success: false, output: format!("{err:#}"), data: None, forced: Vec::new() },
        },
        RequestKind::CheckIn => match handler.check_in() {
            Ok(output) => RequestResult { success: true, output, data: None, forced: Vec::new() },
            Err(err) => RequestResult { success: false, output: format!("{err:#}"), data: None, forced: Vec::new() },
        },
        RequestKind::ForcedPatchRuns => match handler.forced_patch_runs() {
            Ok(forced) => RequestResult {
                success: true,
                output: format!("{} forced patch run(s) collected", forced.len()),
                data: None,
                forced,
            },
            Err(err) => RequestResult { success: false, output: format!("{err:#}"), data: None, forced: Vec::new() },
        },
    }
}

/// How recently a per-user agent must have checked in for the root service to consider this host
/// "somebody's desktop" and leave the patching schedule to them. The per-user process refreshes
/// its heartbeat once per poll tick (`main::AGENT_POLL_INTERVAL`, a minute), so this is generous
/// by an order of magnitude — the cost of being wrong in the other direction is patching a
/// desktop unattended while its user is sitting in front of it.
pub const HEARTBEAT_MAX_AGE: Duration = Duration::from_secs(10 * 60);

/// Says "a per-user agent is alive on this host and is driving the patching schedule".
///
/// This has no counterpart on macOS or Windows, where a managed host is somebody's computer by
/// assumption. On Linux it decides which half of the agent owns the schedule: see
/// `patch_cycle::run_unattended` for why a server with nobody logged in has to be handled at all,
/// and `main::run_daemon` for where the decision is made.
///
/// One file per user id, because the queue directory is sticky (`1733`): two users sharing one
/// file name would mean the second could neither overwrite nor remove the first's.
pub fn record_heartbeat(queue_dir: &Path) {
    // SAFETY: `getuid` cannot fail and touches nothing.
    let uid = unsafe { libc::getuid() };
    let path = queue_dir.join(format!("ui-{uid}.heartbeat"));

    // Written rather than merely touched: `utimensat` on a file this process already owns would
    // do, but writing is one call, needs no extra dependency, and the content is a useful thing to
    // find when reading the directory by hand. Not `fs::write`, though, which follows a symlink:
    // another user can create this name first, and the sticky bit stops this process replacing
    // it. O_NOFOLLOW makes that a logged failure rather than a write into wherever they pointed.
    let written = fs::OpenOptions::new()
        .write(true)
        .create(true)
        .truncate(true)
        .custom_flags(libc::O_NOFOLLOW | libc::O_CLOEXEC)
        .open(&path)
        .and_then(|mut file| file.write_all(now_epoch().to_string().as_bytes()));
    if let Err(err) = written {
        crate::logging::warn(&format!("could not record a heartbeat at {}: {err}", path.display()));
    }
}

/// A live per-user agent, as the root service sees it: which user's, and how long ago it last
/// said so. Both are carried purely so the deferral can name them in the log — deferring is the
/// single decision that can stop a host patching, and 0.5.0 recorded it as one unqualified line
/// with nothing in it to check against.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UiAgentHeartbeat {
    /// The uid the heartbeat file names (see `record_heartbeat`), which is also the uid that owns
    /// the file — a heartbeat whose name and owner disagree is not a heartbeat (see
    /// `live_ui_agent_with`). A diagnostic, never something dispatched on.
    pub uid: String,
    pub age: Duration,
}

/// The freshest per-user heartbeat recorded within `max_age`, if there is one — i.e. "a per-user
/// agent is alive on this host and is driving the patching schedule". Read by the root service
/// only, which is the only thing that can read this directory at all.
///
/// Returns the heartbeat rather than a bare bool so the caller's log line can name whose session
/// it is and how stale the claim is; see `main::patch_unattended_if_nobody_is_logged_in`, the only
/// caller, for why that detail earns its place.
pub fn live_ui_agent(queue_dir: &Path, max_age: Duration) -> Option<UiAgentHeartbeat> {
    live_ui_agent_with(queue_dir, max_age, SystemTime::now(), &uid_has_login_session)
}

/// The testable form: `now` is a parameter, and every uid counts as logged in.
#[cfg(test)]
fn live_ui_agent_at(queue_dir: &Path, max_age: Duration, now: SystemTime) -> Option<UiAgentHeartbeat> {
    live_ui_agent_with(queue_dir, max_age, now, &|_| true)
}

/// Deferring the patching schedule is the one decision in this agent that can leave a host
/// unpatched, and a heartbeat is a file any local user can create. So three things have to hold
/// before one counts, and each closes something that was demonstrated against a live host: the
/// file is owned by the uid its name carries (a forged `ui-424242.heartbeat` was owned by somebody
/// else entirely); that uid has a login session (424242 existed nowhere); and its timestamp is not
/// further ahead of the clock than `max_age` — a `touch -d` into next year read as "0s ago" forever.
fn live_ui_agent_with(queue_dir: &Path, max_age: Duration, now: SystemTime, has_session: SessionCheck) -> Option<UiAgentHeartbeat> {
    let entries = fs::read_dir(queue_dir).ok()?;

    entries
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| path.extension().is_some_and(|extension| extension == "heartbeat"))
        .filter_map(|path| {
            let uid = uid_from_heartbeat_name(&path)?;
            let (_file, metadata) = open_queue_entry(&path).ok()?;
            if metadata.uid() != uid || !has_session(uid) {
                return None;
            }
            let age = heartbeat_age(metadata.modified().ok()?, now, max_age)?;
            Some(UiAgentHeartbeat { uid: uid.to_string(), age })
        })
        .min_by_key(|heartbeat| heartbeat.age)
}

/// How old a heartbeat is, or `None` if it is not live. A heartbeat a little ahead of the clock
/// counts as live with an age of zero: a clock that has just jumped is not evidence that the user
/// went away. One further ahead than `max_age` is not a clock jump a per-user process refreshing
/// every minute would produce, so it is not live — without that bound a future-dated file never
/// ages out at all.
fn heartbeat_age(modified: SystemTime, now: SystemTime, max_age: Duration) -> Option<Duration> {
    match now.duration_since(modified) {
        Ok(age) => (age <= max_age).then_some(age),
        Err(ahead) => (ahead.duration() <= max_age).then_some(Duration::ZERO),
    }
}

/// Pulls the uid back out of a `ui-{uid}.heartbeat` file name — see `record_heartbeat`, which puts
/// it there. A name that does not carry a parseable uid is not a heartbeat at all.
fn uid_from_heartbeat_name(path: &Path) -> Option<u32> {
    path.file_stem()?.to_str()?.strip_prefix("ui-")?.parse().ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Records what it was asked to do and answers however the test needs — the whole point of
    /// `RequestHandler` being a trait.
    #[derive(Default)]
    struct RecordingHandler {
        patched: Vec<String>,
        os_updates_installed: usize,
        plans_requested: usize,
        check_ins: usize,
        forced_collections: usize,
        fail_patches: bool,
    }

    impl RequestHandler for RecordingHandler {
        fn plan(&mut self) -> Result<Plan> {
            self.plans_requested += 1;
            Ok(Plan {
                apps: vec![PlannedApp { application_name: "Firefox".to_string(), latest_version: Some("126.0".to_string()) }],
                os_update_available: true,
            })
        }

        fn patch_application(&mut self, application_name: &str) -> Result<()> {
            self.patched.push(application_name.to_string());
            if self.fail_patches {
                anyhow::bail!("the upgrade script exited non-zero");
            }
            Ok(())
        }

        fn install_os_updates(&mut self) -> Result<()> {
            self.os_updates_installed += 1;
            Ok(())
        }

        fn check_in(&mut self) -> Result<String> {
            self.check_ins += 1;
            Ok("checked in".to_string())
        }

        fn forced_patch_runs(&mut self) -> Result<Vec<ForcedPatchRun>> {
            self.forced_collections += 1;
            Ok(vec![ForcedPatchRun {
                id: "3f1c1d7e-0000-4000-8000-000000000001".to_string(),
                application_name: "Firefox".to_string(),
                platform: "Linux".to_string(),
            }])
        }
    }

    fn scratch_queue(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("kintsugi-queue-test-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn request_kind_round_trips_through_its_file_name() {
        for kind in [RequestKind::Plan, RequestKind::AppPatch, RequestKind::OsUpdate, RequestKind::CheckIn] {
            let name = format!("1756512000-42-0.{}", kind.extension());
            assert_eq!(RequestKind::from_file_name(&name), Some(kind));
        }
    }

    /// "app-patch.request" also ends with "...request"; the dispatch must not confuse the three.
    #[test]
    fn request_kind_extensions_are_distinguishable_from_one_another() {
        assert_eq!(RequestKind::from_file_name("1-2-3.app-patch.request"), Some(RequestKind::AppPatch));
        assert_eq!(RequestKind::from_file_name("1-2-3.os-update.request"), Some(RequestKind::OsUpdate));
        assert_eq!(RequestKind::from_file_name("1-2-3.plan.request"), Some(RequestKind::Plan));
        assert_eq!(RequestKind::from_file_name("notes.txt"), None);
        assert_eq!(RequestKind::from_file_name("1-2-3.plan.request.result.json"), None);
    }

    #[test]
    fn process_queue_dispatches_each_kind_to_the_matching_handler_method() {
        let dir = scratch_queue("dispatch");
        write_request(&dir, RequestKind::Plan, "").unwrap();
        write_request(&dir, RequestKind::AppPatch, "Firefox").unwrap();
        write_request(&dir, RequestKind::OsUpdate, "").unwrap();

        let mut handler = RecordingHandler::default();
        process_queue_at(&dir, &mut handler, SystemTime::now());

        assert_eq!(handler.plans_requested, 1);
        assert_eq!(handler.patched, vec!["Firefox".to_string()]);
        assert_eq!(handler.os_updates_installed, 1);

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn process_queue_removes_every_request_and_leaves_a_result_behind() {
        let dir = scratch_queue("results");
        let request_path = write_request(&dir, RequestKind::AppPatch, "Firefox").unwrap();

        process_queue_at(&dir, &mut RecordingHandler::default(), SystemTime::now());

        assert!(!request_path.exists(), "the request should be removed once answered");
        let result: RequestResult = serde_json::from_str(&fs::read_to_string(result_path_for(&request_path)).unwrap()).unwrap();
        assert!(result.success);

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn process_queue_reports_a_failed_patch_rather_than_panicking() {
        let dir = scratch_queue("failure");
        let request_path = write_request(&dir, RequestKind::AppPatch, "Firefox").unwrap();

        let mut handler = RecordingHandler { fail_patches: true, ..Default::default() };
        process_queue_at(&dir, &mut handler, SystemTime::now());

        let result: RequestResult = serde_json::from_str(&fs::read_to_string(result_path_for(&request_path)).unwrap()).unwrap();
        assert!(!result.success);
        assert!(result.output.contains("exited non-zero"), "the failure reason should reach the caller: {}", result.output);

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn process_queue_answers_a_plan_request_with_the_plan_itself() {
        let dir = scratch_queue("plan-data");
        let request_path = write_request(&dir, RequestKind::Plan, "").unwrap();

        process_queue_at(&dir, &mut RecordingHandler::default(), SystemTime::now());

        let result: RequestResult = serde_json::from_str(&fs::read_to_string(result_path_for(&request_path)).unwrap()).unwrap();
        let plan = result.data.expect("a Plan request must answer with a plan");
        assert_eq!(plan.apps.len(), 1);
        assert!(plan.os_update_available);
        assert_eq!(plan.total(), 2);

        let _ = fs::remove_dir_all(&dir);
    }

    /// The two properties a forced run rests on, and the only place either is enforced: it patches
    /// what was named and nothing else, and it never installs an OS update. `execute` and
    /// `run_patches` both read `os_update_available` off the plan, so clearing it here is what
    /// stops a forced run rebooting a machine over one application. Pinned in all three agents.
    #[test]
    fn narrowing_keeps_only_the_named_applications_and_drops_the_os_update() {
        let plan = Plan {
            apps: vec![
                PlannedApp { application_name: "Firefox".to_string(), latest_version: Some("2.0".to_string()) },
                PlannedApp { application_name: "GIMP".to_string(), latest_version: Some("3.0".to_string()) },
            ],
            os_update_available: true,
        };

        let narrowed = plan.narrowed_to(&["gimp".to_string()]);

        assert_eq!(narrowed.app_names(), vec!["GIMP".to_string()], "matched case-insensitively");
        assert!(!narrowed.os_update_available, "a forced run patches one application, never the OS");
        assert_eq!(narrowed.total(), 1);
    }

    #[test]
    fn narrowing_to_something_this_host_cannot_patch_leaves_nothing_to_do() {
        let plan = Plan {
            apps: vec![PlannedApp { application_name: "Firefox".to_string(), latest_version: None }],
            os_update_available: true,
        };

        assert!(plan.narrowed_to(&["LibreOffice".to_string()]).is_empty());
    }

    #[test]
    fn process_queue_answers_a_forced_patch_runs_request_with_what_the_handler_collected() {
        let dir = scratch_queue("forced");
        let request_path = write_request(&dir, RequestKind::ForcedPatchRuns, "").unwrap();

        let mut handler = RecordingHandler::default();
        process_queue_at(&dir, &mut handler, SystemTime::now());

        assert_eq!(handler.forced_collections, 1);
        let result: RequestResult = serde_json::from_str(&fs::read_to_string(result_path_for(&request_path)).unwrap()).unwrap();
        assert!(result.success);
        assert_eq!(result.forced.len(), 1);
        assert_eq!(result.forced[0].application_name, "Firefox");
        // The two payload fields are separate for a reason — see `RequestResult`. A forced-run
        // answer must not arrive looking like a plan.
        assert!(result.data.is_none(), "a forced-run answer carries no plan");

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn process_queue_answers_a_check_in_request_with_the_handlers_message() {
        let dir = scratch_queue("check-in");
        let request_path = write_request(&dir, RequestKind::CheckIn, "").unwrap();

        let mut handler = RecordingHandler::default();
        process_queue_at(&dir, &mut handler, SystemTime::now());

        assert_eq!(handler.check_ins, 1);
        let result: RequestResult = serde_json::from_str(&fs::read_to_string(result_path_for(&request_path)).unwrap()).unwrap();
        assert!(result.success);
        assert_eq!(result.output, "checked in");
        assert!(result.data.is_none());

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn process_queue_rejects_an_app_patch_request_with_no_application_name() {
        let dir = scratch_queue("empty-body");
        let request_path = write_request(&dir, RequestKind::AppPatch, "").unwrap();

        let mut handler = RecordingHandler::default();
        process_queue_at(&dir, &mut handler, SystemTime::now());

        assert!(handler.patched.is_empty(), "nothing should be patched without a name to patch");
        let result: RequestResult = serde_json::from_str(&fs::read_to_string(result_path_for(&request_path)).unwrap()).unwrap();
        assert!(!result.success);

        let _ = fs::remove_dir_all(&dir);
    }

    /// The whole reason `MAX_REQUEST_AGE` exists: a request left behind by a process that is long
    /// gone must be thrown away unread, never acted on.
    #[test]
    fn process_queue_discards_a_request_older_than_the_maximum_age_without_running_it() {
        let dir = scratch_queue("stale");
        let request_path = write_request(&dir, RequestKind::OsUpdate, "").unwrap();

        let mut handler = RecordingHandler::default();
        process_queue_at(&dir, &mut handler, SystemTime::now() + MAX_REQUEST_AGE + Duration::from_secs(60));

        assert_eq!(handler.os_updates_installed, 0, "a stale request must not be executed");
        assert!(!request_path.exists(), "a stale request should still be cleaned up");
        assert!(!result_path_for(&request_path).exists());

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn process_queue_still_runs_a_request_that_is_merely_a_little_old() {
        let dir = scratch_queue("fresh-enough");
        write_request(&dir, RequestKind::OsUpdate, "").unwrap();

        let mut handler = RecordingHandler::default();
        process_queue_at(&dir, &mut handler, SystemTime::now() + Duration::from_secs(30));

        assert_eq!(handler.os_updates_installed, 1);

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn write_request_refuses_to_create_the_queue_directory_itself() {
        let missing = std::env::temp_dir().join(format!("kintsugi-queue-missing-{}", std::process::id()));
        let _ = fs::remove_dir_all(&missing);

        let error = write_request(&missing, RequestKind::Plan, "").unwrap_err();

        assert!(error.to_string().contains("is not reachable"), "unexpected error: {error}");
        assert!(!missing.exists(), "an unprivileged process must not create the queue directory");
    }

    #[test]
    fn submit_times_out_and_cleans_up_when_nothing_answers() {
        let dir = scratch_queue("timeout");

        let error = submit(&dir, RequestKind::Plan, "", Duration::from_millis(1)).unwrap_err();

        assert!(error.to_string().contains("timed out"), "unexpected error: {error}");
        let leftovers: Vec<_> = fs::read_dir(&dir).unwrap().filter_map(|e| e.ok()).collect();
        assert!(leftovers.is_empty(), "an abandoned request must not be left for the service to find");

        let _ = fs::remove_dir_all(&dir);
    }

    /// What the production entry point does, minus the login-session lookup — a test runs as
    /// whoever runs it, which in a container is root with no `/run/user/0`. The dispatch tests go
    /// through `process_queue_at` for the same reason; the session check has tests of its own.
    fn live(dir: &Path) -> Option<UiAgentHeartbeat> {
        live_ui_agent_at(dir, HEARTBEAT_MAX_AGE, SystemTime::now())
    }

    #[test]
    fn live_ui_agent_sees_a_fresh_heartbeat() {
        let dir = scratch_queue("heartbeat-fresh");
        record_heartbeat(&dir);

        assert!(live(&dir).is_some());

        let _ = fs::remove_dir_all(&dir);
    }

    /// The root service's deferral is the one decision that can leave a host unpatched, and after
    /// 0.5.0 it logs *whose* session is holding the schedule. That only helps if the uid it prints
    /// is this heartbeat's own.
    #[test]
    fn live_ui_agent_reports_the_uid_that_recorded_the_heartbeat() {
        let dir = scratch_queue("heartbeat-uid");
        record_heartbeat(&dir);

        let heartbeat = live(&dir).expect("the heartbeat just written should be live");

        // SAFETY: `getuid` cannot fail and touches nothing — the same call `record_heartbeat` makes.
        assert_eq!(heartbeat.uid, unsafe { libc::getuid() }.to_string());
        assert!(heartbeat.age <= Duration::from_secs(60), "a heartbeat written a moment ago should not read as old");

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn live_ui_agent_ignores_a_heartbeat_that_has_gone_stale() {
        let dir = scratch_queue("heartbeat-stale");
        record_heartbeat(&dir);

        assert!(
            live_ui_agent_at(&dir, HEARTBEAT_MAX_AGE, SystemTime::now() + HEARTBEAT_MAX_AGE + Duration::from_secs(60)).is_none(),
            "a user who logged out hours ago must not keep a server from patching itself"
        );

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn live_ui_agent_finds_nothing_on_a_host_where_nobody_has_ever_logged_in() {
        let dir = scratch_queue("heartbeat-none");

        assert!(live(&dir).is_none());

        let _ = fs::remove_dir_all(&dir);
    }

    /// A heartbeat is not a request; the queue drain must never try to dispatch on one.
    #[test]
    fn process_queue_ignores_heartbeat_files() {
        let dir = scratch_queue("heartbeat-not-a-request");
        record_heartbeat(&dir);

        let mut handler = RecordingHandler::default();
        process_queue_at(&dir, &mut handler, SystemTime::now());

        assert_eq!(handler.plans_requested, 0, "a heartbeat should not be dispatched as a request");
        assert!(handler.patched.is_empty());
        assert_eq!(handler.os_updates_installed, 0);
        assert!(live(&dir).is_some(), "and it should still be there afterwards");

        let _ = fs::remove_dir_all(&dir);
    }

    fn set_modified(path: &Path, when: SystemTime) {
        fs::OpenOptions::new().write(true).open(path).unwrap().set_modified(when).unwrap();
    }

    /// The forgery that held a live host's schedule: a file named for a uid that exists nowhere,
    /// written by somebody else. The owner check alone refuses it.
    #[test]
    fn a_heartbeat_owned_by_someone_other_than_the_uid_it_names_is_ignored() {
        let dir = scratch_queue("heartbeat-forged-owner");
        fs::write(dir.join("ui-424242.heartbeat"), "1").unwrap();

        assert!(live(&dir).is_none(), "a heartbeat is only as good as the ownership of the file behind it");

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_heartbeat_from_a_uid_with_no_login_session_is_ignored() {
        let dir = scratch_queue("heartbeat-no-session");
        record_heartbeat(&dir);

        assert!(live_ui_agent_with(&dir, HEARTBEAT_MAX_AGE, SystemTime::now(), &|_| false).is_none());
        assert!(live_ui_agent_with(&dir, HEARTBEAT_MAX_AGE, SystemTime::now(), &|_| true).is_some(), "the same file counts once its owner is logged in");

        let _ = fs::remove_dir_all(&dir);
    }

    /// `touch -d 2099-01-01` on one's own heartbeat read as "0s ago" on every check-in, for ever.
    #[test]
    fn a_heartbeat_dated_far_in_the_future_is_not_live() {
        let dir = scratch_queue("heartbeat-future");
        record_heartbeat(&dir);
        // SAFETY: `getuid` cannot fail and touches nothing.
        let path = dir.join(format!("ui-{}.heartbeat", unsafe { libc::getuid() }));
        set_modified(&path, SystemTime::now() + HEARTBEAT_MAX_AGE + Duration::from_secs(60));

        assert!(live(&dir).is_none(), "a timestamp no running per-user process could produce must not hold the schedule");

        let _ = fs::remove_dir_all(&dir);
    }

    /// The tolerance that bound replaces: a clock that has just jumped is still not evidence that
    /// the user went away.
    #[test]
    fn a_heartbeat_slightly_ahead_of_the_clock_is_live_with_an_age_of_zero() {
        let dir = scratch_queue("heartbeat-slightly-ahead");
        record_heartbeat(&dir);
        // SAFETY: as above.
        let path = dir.join(format!("ui-{}.heartbeat", unsafe { libc::getuid() }));
        set_modified(&path, SystemTime::now() + Duration::from_secs(30));

        let heartbeat = live(&dir).expect("a small clock jump must not cost a desktop its schedule");
        assert_eq!(heartbeat.age, Duration::ZERO);

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_heartbeat_that_is_a_symlink_is_ignored() {
        let dir = scratch_queue("heartbeat-symlink");
        let target = dir.join("elsewhere");
        fs::write(&target, "1").unwrap();
        // SAFETY: as above.
        std::os::unix::fs::symlink(&target, dir.join(format!("ui-{}.heartbeat", unsafe { libc::getuid() }))).unwrap();

        assert!(live(&dir).is_none());

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_request_from_a_uid_with_no_login_session_is_discarded_unrun() {
        let dir = scratch_queue("request-no-session");
        let request_path = write_request(&dir, RequestKind::OsUpdate, "").unwrap();

        let mut handler = RecordingHandler::default();
        process_queue_with(&dir, &mut handler, SystemTime::now(), &|_| false);

        assert_eq!(handler.os_updates_installed, 0, "a request from an account that is not logged in must not run as root");
        assert!(!request_path.exists(), "and it should not be left for the next trigger to find either");
        assert!(!result_path_for(&request_path).exists());

        let _ = fs::remove_dir_all(&dir);
    }

    /// The request's name is chosen by an unprivileged user, so it may be a symlink to anything root
    /// can read. It must neither be read through nor acted on.
    #[test]
    fn a_request_that_is_a_symlink_is_discarded_unrun() {
        let dir = scratch_queue("request-symlink");
        let target = dir.join("real-body");
        fs::write(&target, "Firefox").unwrap();
        let link = dir.join(format!("1756512000-1-0.{}", RequestKind::AppPatch.extension()));
        std::os::unix::fs::symlink(&target, &link).unwrap();

        let mut handler = RecordingHandler::default();
        process_queue_at(&dir, &mut handler, SystemTime::now());

        assert!(handler.patched.is_empty(), "a symlinked request must not be followed");
        assert!(!link.exists(), "the link itself should be removed");
        assert!(target.exists(), "and never the file it pointed at");

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_request_that_is_not_a_regular_file_is_discarded_unrun() {
        let dir = scratch_queue("request-directory");
        let not_a_file = dir.join(format!("1756512000-1-0.{}", RequestKind::OsUpdate.extension()));
        fs::create_dir(&not_a_file).unwrap();

        let mut handler = RecordingHandler::default();
        process_queue_at(&dir, &mut handler, SystemTime::now());

        assert_eq!(handler.os_updates_installed, 0);

        let _ = fs::remove_dir_all(&dir);
    }

    /// A result file planted under a predicted name — a symlink here, the worst case — is replaced,
    /// never written through: root unlinks whatever is there and creates the result afresh.
    #[test]
    fn a_result_is_written_fresh_rather_than_through_whatever_was_planted_at_its_path() {
        let dir = scratch_queue("result-planted");
        let victim = dir.join("victim");
        fs::write(&victim, "untouched").unwrap();
        let request_path = write_request(&dir, RequestKind::CheckIn, "").unwrap();
        let result_path = result_path_for(&request_path);
        std::os::unix::fs::symlink(&victim, &result_path).unwrap();

        process_queue_at(&dir, &mut RecordingHandler::default(), SystemTime::now());

        assert_eq!(fs::read_to_string(&victim).unwrap(), "untouched", "the planted link's target must not be written");
        assert!(!fs::symlink_metadata(&result_path).unwrap().file_type().is_symlink(), "the result should be a fresh regular file");
        let result: RequestResult = serde_json::from_str(&fs::read_to_string(&result_path).unwrap()).unwrap();
        assert!(result.success);

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn write_result_leaves_the_file_world_readable_whatever_the_umask() {
        let dir = scratch_queue("result-mode");
        let path = dir.join("1-1-0.plan.request.result.json");

        write_result(&path, "{}").unwrap();

        assert_eq!(fs::metadata(&path).unwrap().permissions().mode() & 0o777, 0o644);

        let _ = fs::remove_dir_all(&dir);
    }
}
