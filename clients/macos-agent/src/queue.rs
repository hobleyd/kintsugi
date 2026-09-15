use std::fs;
use std::io::Write;
use std::os::unix::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

use crate::os_update::InstallAuth;

/// The privilege handoff between this agent's two halves — the per-user `--agent` process that
/// decides *when* to patch and shows the dialogs, and the root LaunchDaemon that can actually write
/// to `/Applications` and run `softwareupdate`.
///
/// It started as an OS-update-only channel, because on macOS the per-user process could run every
/// application upgrade itself: it holds this host's identity (unlike the Windows and Linux per-user
/// halves), and Homebrew — the first upgrade mechanism this agent had — *refuses* to run as root.
/// Then AI-authored scripts arrived for applications Homebrew does not manage, and those install
/// into `/Applications` the way the server's prompt tells them to (`installer -pkg ... -target /`,
/// replacing a bundle in place). A bundle that arrived by MDM, by a `.pkg`, or by any installer that
/// asked for an administrator password is owned by `root:wheel`, and the logged-in user's `rm`
/// against it prints `Permission denied` sixty times and leaves the old version in place. So the
/// same split the other two agents have now applies here, for the rows that need it:
///
/// - A **Homebrew** row (`UpgradeStatus::package_manager` names the manager) runs in the per-user
///   process, as it always has. Homebrew's own refusal to run as root is not negotiable, and its
///   installs are user-owned anyway.
/// - An **AI-researched** row (no package manager) is handed to the root daemon through this queue.
///   `upgrade::runs_as_root` is the one place that decision is made, and the daemon re-checks it
///   before running anything: a request naming a Homebrew row is refused rather than run as root.
///
/// The security property the queue was built around holds for the new request kind exactly as it
/// did for the old: **a request never carries anything executable**. An OS-update request has no
/// body and the daemon always runs the same fixed `softwareupdate -i -a`; an app-patch request names
/// an application and nothing else, and the daemon independently fetches that application's upgrade
/// path from the server and verifies its signature against the pinned artifact-signing key before
/// running it (see `upgrade::patch_one`). A malicious or corrupted request file can, at worst, cause
/// an already-approved upgrade to run early — never arbitrary code as root. The Windows agent's
/// `queue.rs` is this module's sibling, and its comment says the same thing for the same reason.
///
/// # The one thing that does cross this boundary: a volume owner's password
///
/// On Apple silicon `softwareupdate -i` will not install a macOS update for root alone — it wants a
/// *volume owner* to authorize it (`--user` / `--stdinpass`), and root is not an APFS cryptographic
/// user. Without one the daemon downloads several gigabytes and then dies on an interactive
/// `Password:` prompt it has no terminal to answer. So an `OsUpdate` request can now be accompanied
/// by the console user's password, collected by the per-user half at patch time (see
/// `dialogs::request_install_password`) and consumed by the daemon.
///
/// It is carried in a **separate sidecar file**, `<request>.auth`, never in the request body, and
/// that separation is deliberate rather than tidiness: the sentence above — a request carries
/// nothing but a name — stays literally true of requests, and anything reading or logging a request
/// body cannot accidentally pick up a credential. The sidecar's own protections are:
///
/// - **Mode `0600`, set atomically at creation** (`OpenOptions::mode`, not a later `chmod`). The
///   queue directory is `root:admin 0770`, so without this any other administrator on the Mac could
///   read it; with it, only the console user who wrote it and root can.
/// - **Taken, not read.** `take_auth` unlinks it in the same breath as reading it, so it exists for
///   the seconds between the daemon waking and the install starting — not for the hours the install
///   then runs.
/// - **Removed with its request, always.** Every path that unlinks a request unlinks the sidecar
///   too (see `remove_request`), including the stale-discard branch and `submit`'s own timeout
///   cleanup; `sweep_orphans` is only the backstop for a daemon that died mid-run.
/// - **Never logged.** `InstallAuth`'s `Debug` is hand-written to redact the password precisely
///   because `{:?}` is this module's house style.
///
/// What this does *not* defend against: another administrator can overwrite the sidecar before the
/// daemon reads it, since the directory is group-writable. That substitutes their own credentials
/// for the console user's, which is a strictly smaller capability than the one they already have —
/// dropping requests into this directory at all. Note also that unlinking a file on APFS does not
/// erase its bytes (it is copy-on-write, so overwriting in place would not either); the protection
/// here is the file mode and the short lifetime, not destruction of the data.
///
/// The directory is `root:admin 0770` (see packaging/install.sh): the logged-in administrator can
/// drop a request, and only root acts on one. The LaunchDaemon's `WatchPaths` names it, so a request
/// wakes the daemon promptly rather than waiting for the hourly check-in — see `checkin_schedule`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RequestKind {
    /// "Run the (already server-signed) upgrade for this one application, as root." Body: the
    /// application name, exactly as reported in the inventory.
    AppPatch,
    /// "Install pending macOS updates." No body — the daemon always runs the same fixed install,
    /// so this can never carry instructions of its own. On Apple silicon it is accompanied by a
    /// `<request>.auth` sidecar holding the volume owner's credentials, which is the only thing in
    /// this protocol that is not just a name; see the module docs for why it is a separate file.
    OsUpdate,
    /// "Check in with the server now" — the menu bar's "Check In Now", see
    /// `checkin_schedule::request_now`. No body. On this platform the file is really a wake-up:
    /// `WatchPaths` starts the daemon, and every daemon invocation begins with the check-in, so
    /// the handler's answer is a confirmation of work already done.
    CheckIn,
}

impl RequestKind {
    /// The file extension a request of this kind is written with. Part of the on-disk protocol
    /// between the two halves: the daemon dispatches on it (see `process_queue`), so these strings
    /// are load-bearing rather than cosmetic.
    fn extension(self) -> &'static str {
        match self {
            RequestKind::AppPatch => "app-patch.request",
            RequestKind::OsUpdate => "os-update.request",
            RequestKind::CheckIn => "check-in.request",
        }
    }

    fn from_file_name(file_name: &str) -> Option<Self> {
        [RequestKind::AppPatch, RequestKind::OsUpdate, RequestKind::CheckIn]
            .into_iter()
            .find(|kind| file_name.ends_with(kind.extension()))
    }

    /// How long a request of this kind is worth waiting for — and, from the other side, how old one
    /// can be while somebody might still be waiting on it.
    ///
    /// **Both sides read it from here, and that coupling is the point.** `submit` blocks for this
    /// long; `is_stale` discards anything older. Split them and the daemon starts discarding
    /// requests their owners are still holding — an install that simply never happens, with a log
    /// line saying it was discarded because nobody was waiting.
    ///
    /// It became per-kind because one bound genuinely could not serve all three: see
    /// [`OS_UPDATE_TIMEOUT`] for the hour that wasn't enough.
    pub fn timeout(self) -> Duration {
        match self {
            RequestKind::AppPatch => APP_PATCH_TIMEOUT,
            RequestKind::OsUpdate => OS_UPDATE_TIMEOUT,
            RequestKind::CheckIn => CHECK_IN_TIMEOUT,
        }
    }
}

/// What the daemon writes back for a completed request.
#[derive(Debug, Serialize, Deserialize)]
pub struct RequestResult {
    pub success: bool,
    pub output: String,
}

/// How long the per-user process waits for the daemon to answer an application upgrade — minutes
/// usually, but a large `.dmg` on a slow link is the same shape of wait.
pub const APP_PATCH_TIMEOUT: Duration = Duration::from_secs(60 * 60);

/// How long it waits for a macOS install, which is a different order of magnitude and used to be
/// the same number as [`APP_PATCH_TIMEOUT`] — that is the bug the error in the log was.
///
/// One hour is not enough and was never going to be. A real run here spent **80 minutes** simply
/// downloading macOS Tahoe 26.7 (2.9GB) before it even reached the install, so the per-user half
/// gave up at exactly 3600s and logged "timed out waiting for the root daemon to answer a OsUpdate
/// request" — while the daemon was still working, and went on to write an answer twenty minutes
/// later that nobody was left to read. The install is `softwareupdate -i -a`, so the download is
/// everything applicable: on that same host the listing also offered macOS 27 at 11.7GB, and six
/// hours is what a host on a slow link needs for the pair of them.
pub const OS_UPDATE_TIMEOUT: Duration = Duration::from_secs(6 * 60 * 60);

/// How long the per-user process waits for the daemon to answer a "Check In Now" request. A
/// check-in is `softwareupdate -l`, the Homebrew inventory, two POSTs and possibly an agent
/// download (`self_update::DOWNLOAD_TIMEOUT`), so minutes rather than seconds — but well short of
/// the other two, because the menu bar reads "Checking in…" for the whole wait and an hour of that
/// for a daemon that never answered would be worse than a reported timeout.
pub const CHECK_IN_TIMEOUT: Duration = Duration::from_secs(10 * 60);


fn now_epoch() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs()
}

fn result_path_for(request_path: &Path) -> PathBuf {
    let mut path = request_path.as_os_str().to_os_string();
    path.push(RESULT_SUFFIX);
    PathBuf::from(path)
}

const RESULT_SUFFIX: &str = ".result.json";
const AUTH_SUFFIX: &str = ".auth";

/// Where an `OsUpdate` request's volume-owner credentials live while it is outstanding — see the
/// module docs. Derived from the request's path rather than carried alongside it so that neither
/// half can look in the wrong place, and so `remove_request` can clean up a sidecar it never saw
/// written.
fn auth_path_for(request_path: &Path) -> PathBuf {
    let mut path = request_path.as_os_str().to_os_string();
    path.push(AUTH_SUFFIX);
    PathBuf::from(path)
}

/// Writes the sidecar, `0600` from the instant it exists.
///
/// `create_new(true).mode(0o600)` rather than create-then-`chmod`: the queue directory is
/// `root:admin 0770`, so a `chmod` a moment later leaves a window in which another administrator
/// could read the password. The two fields are separated by the *first* newline only, so a password
/// containing one still round-trips.
fn write_auth(request_path: &Path, auth: &InstallAuth) -> Result<()> {
    let path = auth_path_for(request_path);
    let mut file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .mode(0o600)
        .open(&path)
        .context("could not create the authorization sidecar")?;

    // Formatted into a local rather than written field-by-field so a partially written sidecar
    // (which `take_auth` would reject anyway) is as unlikely as one `write_all` can make it.
    let contents = format!("{}\n{}", auth.user, auth.password);
    file.write_all(contents.as_bytes()).context("could not write the authorization sidecar")?;
    Ok(())
}

/// Reads an `OsUpdate` request's credentials **and unlinks them**, whether or not they parse.
///
/// "Take" rather than "read" is the contract the module docs rest on: after this returns, the
/// password exists only in this process's memory for the length of the install. Returns `None` when
/// there is no sidecar — an Intel host, where `--user`/`--stdinpass` do not exist, or a request
/// submitted by an older per-user binary mid-upgrade.
fn take_auth(request_path: &Path) -> Option<InstallAuth> {
    let path = auth_path_for(request_path);
    let contents = fs::read_to_string(&path).ok();
    let _ = fs::remove_file(&path);

    let contents = contents?;
    let (user, password) = contents.split_once('\n')?;
    (!user.is_empty() && !password.is_empty())
        .then(|| InstallAuth { user: user.to_string(), password: password.to_string() })
}

/// Unlinks a request and anything that belongs to it.
///
/// Every site that discards a request goes through here — the stale branch, the post-run removal,
/// and `submit`'s own timeout cleanup — so that a credential can never outlive the request it
/// authorizes just because one of them forgot. `sweep_orphans` exists for the case none of those
/// ran at all (the daemon was killed mid-install), not as the normal path.
fn remove_request(request_path: &Path) {
    let _ = fs::remove_file(auth_path_for(request_path));
    let _ = fs::remove_file(request_path);
}

/// Drops a request into the queue and returns its path. The file name carries the process id and a
/// monotonic counter alongside the timestamp so two requests made inside the same second — which a
/// patch cycle does constantly, one per application — can't collide on a name and read each other's
/// results.
fn write_request(queue_dir: &Path, kind: RequestKind, body: &str, auth: Option<&InstallAuth>) -> Result<PathBuf> {
    use std::sync::atomic::{AtomicU64, Ordering};
    static COUNTER: AtomicU64 = AtomicU64::new(0);

    fs::create_dir_all(queue_dir).with_context(|| format!("could not create queue directory {}", queue_dir.display()))?;

    let sequence = COUNTER.fetch_add(1, Ordering::Relaxed);
    let request_path = queue_dir.join(format!("{}-{}-{sequence}.{}", now_epoch(), std::process::id(), kind.extension()));

    if let Some(auth) = auth {
        write_auth(&request_path, auth)?;
    }

    if let Err(err) = fs::write(&request_path, body) {
        // The sidecar is already on disk at this point; nothing will ever come to collect it, so
        // it must not be left behind holding a password.
        let _ = fs::remove_file(auth_path_for(&request_path));
        return Err(err).context("could not write the queue request");
    }

    Ok(request_path)
}

/// Submits a request and blocks (polling) until the daemon writes a result, or `timeout` elapses.
///
/// Polling rather than a directory-change notification on this side on purpose: the per-user
/// process is already blocked on this one step and has nothing else to do, and a 1-second poll of a
/// single known path costs nothing next to the install it's waiting for. The *daemon* side is the
/// one that can't afford to poll, and doesn't — launchd's `WatchPaths` starts it.
pub fn submit(queue_dir: &Path, kind: RequestKind, body: &str) -> Result<RequestResult> {
    submit_with_auth(queue_dir, kind, body, None)
}

/// [`submit`], plus the volume-owner credentials an `OsUpdate` needs on Apple silicon — see the
/// module docs for how they travel and what protects them.
///
/// The sidecar is written **before** the request, and the order matters: the LaunchDaemon's
/// `WatchPaths` names this directory, so the request file appearing is what wakes the daemon. Write
/// them the other way round and a daemon that woke promptly could read a request whose credentials
/// had not landed yet, fall back to the unauthorized path, and spend an hour downloading before
/// failing exactly the way this change exists to stop. (Creating the sidecar wakes the daemon too;
/// that run finds no request it recognizes and exits, which costs nothing.)
pub fn submit_with_auth(queue_dir: &Path, kind: RequestKind, body: &str, auth: Option<&InstallAuth>) -> Result<RequestResult> {
    submit_with_timeout(queue_dir, kind, body, auth, kind.timeout())
}

/// The body of [`submit_with_auth`], with the wait taken as an argument.
///
/// Only the tests pass anything but `kind.timeout()` — a test that actually waited out
/// [`OS_UPDATE_TIMEOUT`] would take six hours. Kept private so no production caller can quietly
/// choose a bound the daemon's `is_stale` does not agree with.
fn submit_with_timeout(
    queue_dir: &Path,
    kind: RequestKind,
    body: &str,
    auth: Option<&InstallAuth>,
    timeout: Duration,
) -> Result<RequestResult> {
    let request_path = write_request(queue_dir, kind, body, auth)?;
    let result_path = result_path_for(&request_path);

    let started = Instant::now();
    loop {
        if let Ok(contents) = fs::read_to_string(&result_path) {
            let _ = fs::remove_file(&result_path);
            return serde_json::from_str(&contents).context("could not parse the queue result written by the root daemon");
        }

        if started.elapsed() >= timeout {
            // Removed so an abandoned request isn't executed long after this process stopped
            // caring about it — a patch starting with no progress window and no warning is worse
            // than one that didn't start. `remove_request`, not a bare unlink, so the credentials
            // go with it rather than sitting in the queue directory until the sweep notices.
            remove_request(&request_path);
            anyhow::bail!(
                "timed out after {}s waiting for the root daemon to answer a {kind:?} request",
                timeout.as_secs()
            );
        }

        std::thread::sleep(Duration::from_secs(1));
    }
}

/// What the daemon does with one request. Implemented by `main::DaemonRequestHandler` (which owns
/// the HTTP client and this host's identity); kept as a trait so `process_queue`'s dispatch,
/// ordering and staleness logic can be tested without any of that.
pub trait RequestHandler {
    fn patch_application(&mut self, application_name: &str) -> Result<()>;
    /// Answers a [`RequestKind::OsUpdate`]. `auth` is whatever the request's sidecar carried, and
    /// is `None` on an Intel host or when the console user could not authorize (see
    /// `patch_cycle`, which then does not submit at all). Returns the message the per-user process
    /// shows, because "installed" and "installed, restart to finish" are different answers and the
    /// person who just typed their password is owed the difference.
    fn install_os_updates(&mut self, auth: Option<InstallAuth>) -> Result<String>;
    /// Answers a [`RequestKind::CheckIn`]. Returns the message the per-user process shows.
    fn check_in(&mut self) -> Result<String>;
}

/// The epoch second the request in `file_name` was written — the leading digits of the name, which
/// is what `write_request` puts first so that lexicographic order is chronological.
fn request_epoch(file_name: &str) -> Option<u64> {
    let digits = file_name.split(|c: char| !c.is_ascii_digit()).next()?;
    digits.parse().ok()
}

/// Whether nobody can still be waiting on this request: it predates the current boot (its owner
/// died with the previous one — this is the case the Windows service's `discard_stale` exists for,
/// which a oneshot daemon has to detect by date instead), or it is older than its owner waits
/// ([`RequestKind::timeout`]). Either way the answer would be written for nobody and the work would
/// start unannounced. A name that carries no readable timestamp is treated as stale too: the
/// protocol always writes one, so its absence means the file is not this agent's.
///
/// The bound is the *kind's* now rather than one shared number, so that raising the OS-update
/// timeout moved both sides of the coupling at once. Discarding on a shorter bound than the owner
/// waits is the failure mode this guards against: the request vanishes, the owner blocks on a
/// result that will never be written, and the log says it was discarded because nobody was waiting.
fn is_stale(file_name: &str, kind: RequestKind, now: u64, boot_epoch: Option<u64>) -> bool {
    let Some(written) = request_epoch(file_name) else {
        return true;
    };
    boot_epoch.is_some_and(|boot| written < boot) || now.saturating_sub(written) > kind.timeout().as_secs()
}

/// When this Mac last booted, from the kernel. `None` if the kernel will not say, in which case the
/// age check alone decides staleness.
fn boot_epoch() -> Option<u64> {
    let mut boot_time = libc::timeval { tv_sec: 0, tv_usec: 0 };
    let mut length = std::mem::size_of::<libc::timeval>();
    // SAFETY: `kern.boottime` is a struct timeval, `length` names the buffer's real size, and the
    // NUL-terminated name outlives the call.
    let status = unsafe {
        libc::sysctlbyname(
            b"kern.boottime\0".as_ptr().cast::<libc::c_char>(),
            (&mut boot_time as *mut libc::timeval).cast::<libc::c_void>(),
            &mut length,
            std::ptr::null_mut(),
            0,
        )
    };
    (status == 0 && boot_time.tv_sec > 0).then(|| boot_time.tv_sec as u64)
}

/// The daemon's half of the handoff: run once per invocation (it's triggered on demand via
/// `WatchPaths` on the queue directory — see the LaunchDaemon plist — so it doesn't need its own
/// persistent loop). Processes every pending request found, oldest first, so a request dropped
/// while the daemon was already mid-run isn't silently skipped; discards, without running, any
/// request nobody can still be waiting on — see `is_stale`.
///
/// Runs to completion for each request before moving on, deliberately: two installers at once
/// would fight over `/Applications` and the per-user process asks for one application at a time
/// anyway.
pub fn process_queue(queue_dir: &Path, handler: &mut impl RequestHandler) {
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

    let now = now_epoch();
    let boot = boot_epoch();

    for request_path in requests {
        let Some((file_name, kind)) = request_path
            .file_name()
            .and_then(|name| name.to_str())
            .and_then(|name| RequestKind::from_file_name(name).map(|kind| (name, kind)))
        else {
            continue;
        };

        if is_stale(file_name, kind, now, boot) {
            crate::logging::warn(&format!(
                "discarding a {kind:?} request nobody is waiting on any more: {}",
                request_path.display()
            ));
            remove_request(&request_path);
            continue;
        }

        crate::logging::info(&format!("processing {kind:?} request: {}", request_path.display()));

        // Note what an `OsUpdate` can do from here: `os_update::install` passes `-R`, so a
        // successful macOS install reboots this Mac from inside the call and nothing below ever
        // runs. The request file therefore survives the reboot — and is discarded unrun at the next
        // boot by `is_stale`'s boot check, which is exactly the case that check exists for. The
        // credentials do not survive it, because `take_auth` on the next line unlinks the sidecar
        // before the install starts rather than after it finishes.
        let body = fs::read_to_string(&request_path).unwrap_or_default();
        // Taken before the handler runs, and gone from disk from here on — the install it
        // authorizes can last hours.
        let auth = (kind == RequestKind::OsUpdate).then(|| take_auth(&request_path)).flatten();
        let result = run_request(kind, body.trim(), auth, handler);

        crate::logging::info(&format!(
            "{kind:?} request finished: success={} {}",
            result.success,
            result.output.trim()
        ));

        // The request is removed first, and the result written second: the per-user process waits
        // on the result file, so writing it while the request still existed would leave a window
        // where a crash here re-runs a request that has already been answered.
        remove_request(&request_path);
        if let Ok(json) = serde_json::to_string(&result) {
            if let Err(err) = fs::write(result_path_for(&request_path), json) {
                crate::logging::warn(&format!("could not write the queue result: {err}"));
            }
        }
    }

    sweep_orphans(queue_dir, now, boot);
}

/// Deletes what the ordinary paths could not.
///
/// Two kinds of litter accumulate here, and before this existed nothing ever removed either:
///
/// - **Results nobody collected.** `submit` removes its *request* when it times out but cannot
///   remove a result that had not been written yet — and the daemon writes one regardless, because
///   from its side the work did finish. That is how a **109KB** `os-update.request.result.json` came
///   to sit in this directory indefinitely: the waiter gave up at an hour, the daemon answered at
///   an hour and twenty, and the answer stayed. A result is only useful to the request that spawned
///   it, so once that request could no longer be outstanding, neither can its answer.
/// - **Credentials whose daemon died.** `remove_request` handles every orderly path; this is the
///   backstop for a machine that lost power mid-install. Aged out on the *request's* bound rather
///   than something shorter so the sidecar of a genuinely outstanding request is never taken out
///   from under it.
///
/// Anything in here whose name this protocol did not write is left strictly alone — the directory
/// belongs to the fleet's administrator as much as to the agent.
fn sweep_orphans(queue_dir: &Path, now: u64, boot: Option<u64>) {
    let Ok(entries) = fs::read_dir(queue_dir) else {
        return;
    };

    for entry in entries.filter_map(|entry| entry.ok()) {
        let path = entry.path();
        let Some(file_name) = path.file_name().and_then(|name| name.to_str()) else {
            continue;
        };

        // The suffix is stripped before the kind is read, because `from_file_name` matches on the
        // *end* of the name — which is what keeps a result file from being mistaken for a request
        // in the loop above.
        let Some(stem) = file_name.strip_suffix(RESULT_SUFFIX).or_else(|| file_name.strip_suffix(AUTH_SUFFIX)) else {
            continue;
        };
        let Some(kind) = RequestKind::from_file_name(stem) else {
            continue;
        };

        if is_stale(stem, kind, now, boot) {
            crate::logging::info(&format!("sweeping an orphaned queue file nothing can still want: {}", path.display()));
            let _ = fs::remove_file(&path);
        }
    }
}

fn run_request(kind: RequestKind, body: &str, auth: Option<InstallAuth>, handler: &mut impl RequestHandler) -> RequestResult {
    match kind {
        RequestKind::AppPatch => {
            if body.is_empty() {
                return RequestResult { success: false, output: "no application name in the request".to_string() };
            }
            match handler.patch_application(body) {
                Ok(()) => RequestResult { success: true, output: format!("patched {body}") },
                Err(err) => RequestResult { success: false, output: format!("{err:#}") },
            }
        }
        RequestKind::OsUpdate => match handler.install_os_updates(auth) {
            Ok(output) => RequestResult { success: true, output },
            Err(err) => RequestResult { success: false, output: format!("{err:#}") },
        },
        RequestKind::CheckIn => match handler.check_in() {
            Ok(output) => RequestResult { success: true, output },
            Err(err) => RequestResult { success: false, output: format!("{err:#}") },
        },
    }
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
        /// What the daemon side was handed for the last OS-update request — the property the
        /// sidecar exists to deliver.
        os_update_auth: Option<InstallAuth>,
        check_ins: usize,
        fail_patches: bool,
    }

    impl RequestHandler for RecordingHandler {
        fn patch_application(&mut self, application_name: &str) -> Result<()> {
            self.patched.push(application_name.to_string());
            if self.fail_patches {
                anyhow::bail!("the upgrade script exited non-zero");
            }
            Ok(())
        }

        fn install_os_updates(&mut self, auth: Option<InstallAuth>) -> Result<String> {
            self.os_updates_installed += 1;
            self.os_update_auth = auth;
            Ok("installed pending macOS updates".to_string())
        }

        fn check_in(&mut self) -> Result<String> {
            self.check_ins += 1;
            Ok("checked in".to_string())
        }
    }

    fn scratch_dir(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("kintsugi-queue-test-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn read_result(request: &Path) -> RequestResult {
        serde_json::from_str(&fs::read_to_string(result_path_for(request)).unwrap()).unwrap()
    }

    #[test]
    fn request_kind_round_trips_through_its_file_name() {
        for kind in [RequestKind::AppPatch, RequestKind::OsUpdate, RequestKind::CheckIn] {
            let name = format!("1700000000-42-0.{}", kind.extension());
            assert_eq!(RequestKind::from_file_name(&name), Some(kind));
        }
    }

    #[test]
    fn request_kind_ignores_a_result_file_and_anything_unrelated() {
        // process_queue must never treat its own result files as new work, or answering a request
        // would immediately queue another one.
        assert_eq!(RequestKind::from_file_name("1700000000-42-0.app-patch.request.result.json"), None);
        assert_eq!(RequestKind::from_file_name("notes.txt"), None);
    }

    #[test]
    fn process_queue_passes_the_application_name_through_for_an_app_patch() {
        let dir = scratch_dir("patch");
        let request = write_request(&dir, RequestKind::AppPatch, "Ollama", None).unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);

        assert_eq!(handler.patched, vec!["Ollama".to_string()]);
        assert!(read_result(&request).success);
    }

    #[test]
    fn process_queue_reports_a_failed_patch_rather_than_swallowing_it() {
        let dir = scratch_dir("patch-fail");
        let request = write_request(&dir, RequestKind::AppPatch, "Ollama", None).unwrap();
        let mut handler = RecordingHandler { fail_patches: true, ..Default::default() };

        process_queue(&dir, &mut handler);

        let result = read_result(&request);
        assert!(!result.success);
        assert!(result.output.contains("exited non-zero"));
    }

    #[test]
    fn process_queue_rejects_an_app_patch_with_no_application_name() {
        let dir = scratch_dir("patch-empty");
        let request = write_request(&dir, RequestKind::AppPatch, "  ", None).unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);

        assert!(!read_result(&request).success);
        assert!(handler.patched.is_empty());
    }

    #[test]
    fn process_queue_removes_every_request_it_handled() {
        // A request left behind would be re-run on the daemon's next pass — for an app patch, that
        // means installing the same upgrade over and over.
        let dir = scratch_dir("cleanup");
        write_request(&dir, RequestKind::OsUpdate, "", None).unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);
        process_queue(&dir, &mut handler);

        assert_eq!(handler.os_updates_installed, 1);
    }

    #[test]
    fn process_queue_answers_a_check_in_request_with_the_handlers_message() {
        let dir = scratch_dir("check-in");
        let request = write_request(&dir, RequestKind::CheckIn, "", None).unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);

        assert_eq!(handler.check_ins, 1);
        let result = read_result(&request);
        assert!(result.success);
        assert_eq!(result.output, "checked in");
    }

    #[test]
    fn process_queue_handles_requests_oldest_first() {
        let dir = scratch_dir("ordering");
        // Written by hand rather than via write_request so the timestamps are controlled — recent
        // enough not to be stale, whatever the clock says.
        let base = now_epoch() - 30;
        fs::write(dir.join(format!("{}-1-0.app-patch.request", base + 2)), "Third").unwrap();
        fs::write(dir.join(format!("{}-1-0.app-patch.request", base)), "First").unwrap();
        fs::write(dir.join(format!("{}-1-0.app-patch.request", base + 1)), "Second").unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);

        assert_eq!(handler.patched, vec!["First".to_string(), "Second".to_string(), "Third".to_string()]);
    }

    #[test]
    fn process_queue_discards_a_request_nobody_is_waiting_on() {
        // The failure this guards: the Mac was shut down mid-patch, and at the next boot the daemon
        // finds the request its owner never got to remove. Running it would install an upgrade with
        // nothing on screen saying so.
        let dir = scratch_dir("stale");
        let path = dir.join(format!("{}-1-0.app-patch.request", now_epoch() - APP_PATCH_TIMEOUT.as_secs() - 60));
        fs::write(&path, "Ollama").unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);

        assert!(handler.patched.is_empty());
        assert!(!path.exists(), "a stale request must be removed, not left for the next pass");
        assert!(!result_path_for(&path).exists(), "nobody is waiting, so no result is written");
    }

    #[test]
    fn is_stale_rejects_anything_from_before_this_boot_or_older_than_the_wait() {
        let now = 1_800_000_000;
        let boot = Some(1_799_990_000);
        let kind = RequestKind::AppPatch;

        assert!(!is_stale("1799998000-1-0.app-patch.request", kind, now, boot), "half an hour old, written after boot");
        // Recent enough by age alone; only the boot check can catch it.
        assert!(is_stale("1799998999-1-0.app-patch.request", kind, now, Some(1_799_999_000)), "written one second before boot");
        assert!(is_stale(&format!("{}-1-0.app-patch.request", now - APP_PATCH_TIMEOUT.as_secs() - 1), kind, now, None));
        assert!(!is_stale(&format!("{}-1-0.app-patch.request", now - APP_PATCH_TIMEOUT.as_secs()), kind, now, None));
        // The OS-update-only queue named its requests with the bare epoch; those files are still
        // dated, so a daemon self-updated mid-wait still answers one that is being waited on.
        assert!(!is_stale("1799998000.app-patch.request", kind, now, boot));
        assert!(is_stale("untimestamped.app-patch.request", kind, now, boot));
    }

    /// The regression the per-kind timeout exists for. An OS-update request that is two hours old
    /// is still being waited on — the download alone can take longer than that — and discarding it
    /// on the application bound would have left its owner blocked on a result that never comes.
    #[test]
    fn is_stale_gives_an_os_update_the_hours_its_download_needs() {
        let now = 1_800_000_000;
        let two_hours_ago = format!("{}-1-0.os-update.request", now - 2 * 60 * 60);

        assert!(is_stale(&two_hours_ago, RequestKind::AppPatch, now, None), "an application upgrade is long gone by then");
        assert!(!is_stale(&two_hours_ago, RequestKind::OsUpdate, now, None), "a macOS install is very much still running");
        assert!(is_stale(&format!("{}-1-0.os-update.request", now - OS_UPDATE_TIMEOUT.as_secs() - 1), RequestKind::OsUpdate, now, None));
    }

    #[test]
    fn submit_times_out_and_removes_its_request_when_no_daemon_is_running() {
        // The failure mode this guards: the daemon never answers. The per-user process has to give
        // up *and* clean up, or the request would be executed unannounced whenever the daemon next
        // runs.
        let dir = scratch_dir("timeout");

        let result = submit_with_timeout(&dir, RequestKind::OsUpdate, "", None, Duration::from_secs(0));

        assert!(result.is_err());
        assert_eq!(fs::read_dir(&dir).unwrap().count(), 0);
    }

    fn auth() -> InstallAuth {
        InstallAuth { user: "david".to_string(), password: "hunter2, with a comma".to_string() }
    }

    /// The security property the whole sidecar arrangement rests on. The queue directory is
    /// `root:admin 0770`, so without `0600` every other administrator on the Mac could read a
    /// volume owner's password out of it.
    #[test]
    fn the_authorization_sidecar_is_only_readable_by_its_writer_and_root() {
        use std::os::unix::fs::PermissionsExt;

        let dir = scratch_dir("auth-mode");
        let request = write_request(&dir, RequestKind::OsUpdate, "", Some(&auth())).unwrap();

        let mode = fs::metadata(auth_path_for(&request)).unwrap().permissions().mode() & 0o777;

        assert_eq!(mode, 0o600, "the sidecar was {mode:o}, which lets the admin group read the password");
    }

    #[test]
    fn taking_the_authorization_reads_it_once_and_unlinks_it() {
        let dir = scratch_dir("auth-take");
        let request = write_request(&dir, RequestKind::OsUpdate, "", Some(&auth())).unwrap();

        let taken = take_auth(&request).expect("the sidecar was just written");

        assert_eq!(taken.user, "david");
        assert_eq!(taken.password, "hunter2, with a comma", "a password is taken verbatim, commas and all");
        assert!(!auth_path_for(&request).exists(), "the credentials must not outlive the read");
        assert!(take_auth(&request).is_none(), "and a second take finds nothing");
    }

    /// End to end through the queue: what the per-user half hands to `submit_with_auth` is what the
    /// daemon's handler is called with.
    #[test]
    fn process_queue_hands_an_os_update_the_credentials_its_request_carried() {
        let dir = scratch_dir("auth-handoff");
        let request = write_request(&dir, RequestKind::OsUpdate, "", Some(&auth())).unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);

        let received = handler.os_update_auth.as_ref().expect("the handler should have been authorized");
        assert_eq!(received.user, "david");
        assert_eq!(received.password, "hunter2, with a comma");
        assert!(!auth_path_for(&request).exists(), "and nothing is left on disk afterwards");
    }

    /// Every path that discards a request has to take the credentials with it — an abandoned
    /// password sitting in the queue directory until the sweep notices is the thing the sidecar's
    /// short lifetime is supposed to prevent.
    #[test]
    fn a_discarded_request_never_leaves_its_credentials_behind() {
        let dir = scratch_dir("auth-discard");

        // 1. The owner gave up waiting.
        let _ = submit_with_timeout(&dir, RequestKind::OsUpdate, "", Some(&auth()), Duration::from_secs(0));
        assert_eq!(fs::read_dir(&dir).unwrap().count(), 0, "submit's timeout cleanup left something behind");

        // 2. The daemon found a request from before this boot and discarded it unrun.
        let stale = dir.join(format!("{}-1-0.os-update.request", now_epoch() - OS_UPDATE_TIMEOUT.as_secs() - 60));
        fs::write(&stale, "").unwrap();
        write_auth(&stale, &auth()).unwrap();

        process_queue(&dir, &mut RecordingHandler::default());

        assert!(!auth_path_for(&stale).exists(), "the stale-discard path left a password on disk");
        assert_eq!(fs::read_dir(&dir).unwrap().count(), 0);
    }

    /// The 109KB file that sat in this directory for a day: the waiter timed out at an hour, the
    /// daemon answered at an hour and twenty, and nothing ever removed the answer.
    #[test]
    fn sweep_removes_a_result_nobody_ever_collected() {
        let dir = scratch_dir("sweep");
        let now = now_epoch();

        let abandoned = dir.join(format!("{}-1-0.os-update.request", now - OS_UPDATE_TIMEOUT.as_secs() - 60));
        fs::write(result_path_for(&abandoned), "{\"success\":false,\"output\":\"...\"}").unwrap();

        let fresh = dir.join(format!("{now}-1-1.os-update.request"));
        fs::write(result_path_for(&fresh), "{\"success\":true,\"output\":\"done\"}").unwrap();

        // Not ours, and not for this code to tidy up.
        let unrelated = dir.join("notes.txt");
        fs::write(&unrelated, "leave me alone").unwrap();

        sweep_orphans(&dir, now, None);

        assert!(!result_path_for(&abandoned).exists(), "an answer nobody can still want should be swept");
        assert!(result_path_for(&fresh).exists(), "a result its owner is still polling for must survive");
        assert!(unrelated.exists(), "the queue directory is the administrator's too");
    }

    #[test]
    fn two_requests_made_in_the_same_second_do_not_collide() {
        // A patch cycle submits one request per application back to back; colliding names would
        // make one application read another's result.
        let dir = scratch_dir("collision");

        let first = write_request(&dir, RequestKind::AppPatch, "A", None).unwrap();
        let second = write_request(&dir, RequestKind::AppPatch, "B", None).unwrap();

        assert_ne!(first, second);
        assert_eq!(fs::read_dir(&dir).unwrap().count(), 2);
    }
}
