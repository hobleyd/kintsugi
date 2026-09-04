use std::fs;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

/// The privilege handoff between this agent's two halves, and the reason it exists at all.
///
/// The macOS agent's handoff is partial: its per-user process holds this host's identity and runs
/// Homebrew upgrades itself (Homebrew refuses to run as root), handing the daemon only what needs
/// root — an OS update, and an AI-researched script against a root-owned `/Applications` bundle
/// (see its `upgrade::runs_as_root`). On Windows nothing of that is possible unprivileged:
///
/// - **Running an upgrade** writes to `%ProgramFiles%` and the machine-wide registry. `msiexec /qn`,
///   `winget upgrade` for a machine-scope package, and anything `choco` does all need elevation,
///   and there is no per-user equivalent that would work instead.
/// - **Asking what's pending** needs this host's mutual-TLS identity, and that identity is
///   deliberately locked to SYSTEM and Administrators (see `config::identity_dir`) so the client
///   private key isn't readable by whoever happens to be logged in.
///
/// So on Windows the tray process performs no privileged action and makes no network call at all:
/// it decides *when* to patch (policy, schedule, the confirm/delay dialog, progress) and asks the
/// service to do each step. The security property the macOS queue was built around is preserved
/// exactly, and in fact strengthened: **a request never carries anything executable**. An app-patch
/// request names an application and nothing else; the service independently fetches that
/// application's upgrade path from the server and verifies its signature before running anything
/// (see `upgrade::patch_one`). A malicious or corrupted request file can, at worst, cause an
/// already-approved upgrade to run early — never arbitrary code as SYSTEM.
///
/// The directory is writable by `BUILTIN\Users` and acted on only by the service, mirroring the
/// macOS queue's `root:admin 0770` — see packaging/install.ps1.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RequestKind {
    /// "What is there to patch right now?" — answered from the server, with this host's identity.
    Plan,
    /// "Run the (already server-signed) upgrade for this one application." Body: the application
    /// name, exactly as reported in the inventory.
    AppPatch,
    /// "Install pending Windows updates." No body — the service always runs the same fixed
    /// install, so this can never carry instructions of its own.
    OsUpdate,
}

impl RequestKind {
    /// The file extension a request of this kind is written with. Part of the on-disk protocol
    /// between the two halves: the service dispatches on it (see `process_queue`), so these strings
    /// are load-bearing rather than cosmetic.
    fn extension(self) -> &'static str {
        match self {
            RequestKind::Plan => "plan.request",
            RequestKind::AppPatch => "app-patch.request",
            RequestKind::OsUpdate => "os-update.request",
        }
    }

    fn from_file_name(file_name: &str) -> Option<Self> {
        for kind in [RequestKind::Plan, RequestKind::AppPatch, RequestKind::OsUpdate] {
            if file_name.ends_with(kind.extension()) {
                return Some(kind);
            }
        }
        None
    }
}

/// What the service writes back for a completed request. `data` carries a `Plan`'s answer and is
/// absent for the two action kinds, which have nothing to report beyond success and output.
#[derive(Debug, Serialize, Deserialize)]
pub struct RequestResult {
    pub success: bool,
    pub output: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub data: Option<Plan>,
}

/// One application the server says is patchable on this host, reduced to just what the tray process
/// needs to show progress. Deliberately not the full `UpgradeStatus`: the tray never sees a script,
/// a command, or a signature, because it never runs one.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PlannedApp {
    pub application_name: String,
    pub latest_version: Option<String>,
}

/// The answer to a [`RequestKind::Plan`] request — the Windows equivalent of what
/// `patch_cycle::plan` computes inline on macOS.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct Plan {
    pub apps: Vec<PlannedApp>,
    pub os_update_available: bool,
}

impl Plan {
    /// The names the confirmation dialog lists, in the order they will be patched — see
    /// `dialogs::confirm_message`.
    pub fn app_names(&self) -> Vec<String> {
        self.apps.iter().map(|app| app.application_name.clone()).collect()
    }

    pub fn total(&self) -> usize {
        self.apps.len() + usize::from(self.os_update_available)
    }

    pub fn is_empty(&self) -> bool {
        self.total() == 0
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

/// Drops a request into the queue and returns its path. The file name carries the process id and a
/// monotonic counter alongside the timestamp so two requests made inside the same second — which a
/// patch cycle does constantly, one per application — can't collide on a name and read each other's
/// results.
fn write_request(queue_dir: &Path, kind: RequestKind, body: &str) -> Result<PathBuf> {
    use std::sync::atomic::{AtomicU64, Ordering};
    static COUNTER: AtomicU64 = AtomicU64::new(0);

    fs::create_dir_all(queue_dir).with_context(|| format!("could not create queue directory {}", queue_dir.display()))?;

    let sequence = COUNTER.fetch_add(1, Ordering::Relaxed);
    let request_path = queue_dir.join(format!("{}-{}-{sequence}.{}", now_epoch(), std::process::id(), kind.extension()));
    fs::write(&request_path, body).context("could not write the queue request")?;
    Ok(request_path)
}

/// Submits a request and blocks (polling) until the service writes a result, or `timeout` elapses.
///
/// Polling rather than a directory-change notification on this side on purpose: the tray process is
/// already blocked on this one step and has nothing else to do, and a 1-second poll of a single
/// known path costs nothing next to the install it's waiting for. The *service* side is the one
/// that can't afford to poll, and doesn't — see `service`'s directory watch.
pub fn submit(queue_dir: &Path, kind: RequestKind, body: &str, timeout: Duration) -> Result<RequestResult> {
    let request_path = write_request(queue_dir, kind, body)?;
    let result_path = result_path_for(&request_path);

    let started = Instant::now();
    loop {
        if let Ok(contents) = fs::read_to_string(&result_path) {
            let _ = fs::remove_file(&result_path);
            return serde_json::from_str(&contents).context("could not parse the queue result written by the service");
        }

        if started.elapsed() >= timeout {
            // Removed so an abandoned request isn't executed long after the tray process stopped
            // caring about it — a patch starting with no progress window and no warning is worse
            // than one that didn't start.
            let _ = fs::remove_file(&request_path);
            anyhow::bail!("timed out waiting for the kintsugi-agent service to answer a {kind:?} request");
        }

        std::thread::sleep(Duration::from_secs(1));
    }
}

/// What the service does with one request. Implemented by `service` (which owns the HTTP client and
/// this host's identity); kept as a trait so `process_queue`'s dispatch and ordering logic can be
/// tested without any of that.
pub trait RequestHandler {
    fn plan(&mut self) -> Result<Plan>;
    fn patch_application(&mut self, application_name: &str) -> Result<()>;
    fn install_os_updates(&mut self) -> Result<()>;
}

/// The service's half of the handoff: processes every pending request found, oldest first, so a
/// request dropped while the service was already mid-run isn't silently skipped.
///
/// Runs to completion for each request before moving on, deliberately: two `winget upgrade`
/// invocations at once would fight over the same package cache, and the tray process asks for one
/// application at a time anyway.
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

    for request_path in requests {
        let Some(kind) = request_path
            .file_name()
            .and_then(|name| name.to_str())
            .and_then(RequestKind::from_file_name)
        else {
            continue;
        };

        crate::logging::info(&format!("processing {kind:?} request: {}", request_path.display()));

        let body = fs::read_to_string(&request_path).unwrap_or_default();
        let result = run_request(kind, body.trim(), handler);

        crate::logging::info(&format!(
            "{kind:?} request finished: success={} {}",
            result.success,
            result.output.trim()
        ));

        // The request is removed first, and the result written second: the tray process waits on
        // the result file, so writing it while the request still existed would leave a window where
        // a crash here re-runs a request that has already been answered.
        let _ = fs::remove_file(&request_path);
        if let Ok(json) = serde_json::to_string(&result) {
            if let Err(err) = fs::write(result_path_for(&request_path), json) {
                crate::logging::warn(&format!("could not write the queue result: {err}"));
            }
        }
    }
}

fn run_request(kind: RequestKind, body: &str, handler: &mut impl RequestHandler) -> RequestResult {
    match kind {
        RequestKind::Plan => match handler.plan() {
            Ok(plan) => RequestResult {
                success: true,
                output: format!("{} application(s) pending, os_update_available={}", plan.apps.len(), plan.os_update_available),
                data: Some(plan),
            },
            Err(err) => RequestResult { success: false, output: format!("{err:#}"), data: None },
        },
        RequestKind::AppPatch => {
            if body.is_empty() {
                return RequestResult { success: false, output: "no application name in the request".to_string(), data: None };
            }
            match handler.patch_application(body) {
                Ok(()) => RequestResult { success: true, output: format!("patched {body}"), data: None },
                Err(err) => RequestResult { success: false, output: format!("{err:#}"), data: None },
            }
        }
        RequestKind::OsUpdate => match handler.install_os_updates() {
            Ok(()) => RequestResult { success: true, output: "installed pending Windows updates".to_string(), data: None },
            Err(err) => RequestResult { success: false, output: format!("{err:#}"), data: None },
        },
    }
}

/// Deletes every request and result left in the queue. Called once by the service at startup: a
/// request written before a reboot has an owner that is long gone, and acting on it would start an
/// unannounced patch cycle with nothing showing progress.
pub fn discard_stale(queue_dir: &Path) {
    let Ok(entries) = fs::read_dir(queue_dir) else {
        return;
    };

    for path in entries.filter_map(|entry| entry.ok()).map(|entry| entry.path()) {
        let is_ours = path
            .file_name()
            .and_then(|name| name.to_str())
            .is_some_and(|name| RequestKind::from_file_name(name).is_some() || name.ends_with(".result.json"));
        if is_ours {
            if let Err(err) = fs::remove_file(&path) {
                crate::logging::warn(&format!("could not discard the stale queue entry {}: {err}", path.display()));
            }
        }
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
        plans_requested: usize,
        fail_patches: bool,
    }

    impl RequestHandler for RecordingHandler {
        fn plan(&mut self) -> Result<Plan> {
            self.plans_requested += 1;
            Ok(Plan {
                apps: vec![PlannedApp { application_name: "Firefox".to_string(), latest_version: Some("154.0.1".to_string()) }],
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
    }

    fn scratch_dir(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("kintsugi-queue-test-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn request_kind_round_trips_through_its_file_name() {
        for kind in [RequestKind::Plan, RequestKind::AppPatch, RequestKind::OsUpdate] {
            let name = format!("1700000000-42-0.{}", kind.extension());
            assert_eq!(RequestKind::from_file_name(&name), Some(kind));
        }
    }

    #[test]
    fn request_kind_ignores_a_result_file_and_anything_unrelated() {
        // process_queue must never treat its own result files as new work, or answering a request
        // would immediately queue another one.
        assert_eq!(RequestKind::from_file_name("1700000000-42-0.plan.request.result.json"), None);
        assert_eq!(RequestKind::from_file_name("notes.txt"), None);
    }

    #[test]
    fn process_queue_answers_a_plan_request_with_the_handlers_plan() {
        let dir = scratch_dir("plan");
        let request = write_request(&dir, RequestKind::Plan, "").unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);

        let result: RequestResult = serde_json::from_str(&fs::read_to_string(result_path_for(&request)).unwrap()).unwrap();
        assert!(result.success);
        let plan = result.data.expect("a plan request must answer with a plan");
        assert_eq!(plan.apps.len(), 1);
        assert!(plan.os_update_available);
        assert_eq!(plan.total(), 2);
    }

    #[test]
    fn process_queue_passes_the_application_name_through_for_an_app_patch() {
        let dir = scratch_dir("patch");
        write_request(&dir, RequestKind::AppPatch, "Mozilla Firefox").unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);

        assert_eq!(handler.patched, vec!["Mozilla Firefox".to_string()]);
    }

    #[test]
    fn process_queue_reports_a_failed_patch_rather_than_swallowing_it() {
        let dir = scratch_dir("patch-fail");
        let request = write_request(&dir, RequestKind::AppPatch, "Firefox").unwrap();
        let mut handler = RecordingHandler { fail_patches: true, ..Default::default() };

        process_queue(&dir, &mut handler);

        let result: RequestResult = serde_json::from_str(&fs::read_to_string(result_path_for(&request)).unwrap()).unwrap();
        assert!(!result.success);
        assert!(result.output.contains("exited non-zero"));
    }

    #[test]
    fn process_queue_rejects_an_app_patch_with_no_application_name() {
        let dir = scratch_dir("patch-empty");
        let request = write_request(&dir, RequestKind::AppPatch, "  ").unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);

        let result: RequestResult = serde_json::from_str(&fs::read_to_string(result_path_for(&request)).unwrap()).unwrap();
        assert!(!result.success);
        assert!(handler.patched.is_empty());
    }

    #[test]
    fn process_queue_removes_every_request_it_handled() {
        // A request left behind would be re-run on the service's next pass — for an app patch, that
        // means installing the same upgrade over and over.
        let dir = scratch_dir("cleanup");
        write_request(&dir, RequestKind::OsUpdate, "").unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);
        process_queue(&dir, &mut handler);

        assert_eq!(handler.os_updates_installed, 1);
    }

    #[test]
    fn process_queue_handles_requests_oldest_first() {
        let dir = scratch_dir("ordering");
        // Written by hand rather than via write_request so the timestamps are controlled.
        fs::write(dir.join("1700000002-1-0.app-patch.request"), "Third").unwrap();
        fs::write(dir.join("1700000000-1-0.app-patch.request"), "First").unwrap();
        fs::write(dir.join("1700000001-1-0.app-patch.request"), "Second").unwrap();
        let mut handler = RecordingHandler::default();

        process_queue(&dir, &mut handler);

        assert_eq!(handler.patched, vec!["First".to_string(), "Second".to_string(), "Third".to_string()]);
    }

    #[test]
    fn discard_stale_clears_requests_and_results_but_leaves_anything_else() {
        let dir = scratch_dir("discard");
        write_request(&dir, RequestKind::OsUpdate, "").unwrap();
        fs::write(dir.join("1700000000-1-0.plan.request.result.json"), "{}").unwrap();
        fs::write(dir.join("README.txt"), "not ours").unwrap();

        discard_stale(&dir);

        let remaining: Vec<String> = fs::read_dir(&dir)
            .unwrap()
            .filter_map(|e| e.ok())
            .map(|e| e.file_name().to_string_lossy().to_string())
            .collect();
        assert_eq!(remaining, vec!["README.txt".to_string()]);
    }

    #[test]
    fn submit_times_out_and_removes_its_request_when_no_service_is_running() {
        // The failure mode this guards: the service is stopped, so nothing ever answers. The tray
        // process has to give up *and* clean up, or the request would be executed unannounced
        // whenever the service next starts.
        let dir = scratch_dir("timeout");

        let result = submit(&dir, RequestKind::Plan, "", Duration::from_secs(0));

        assert!(result.is_err());
        assert_eq!(fs::read_dir(&dir).unwrap().count(), 0);
    }

    #[test]
    fn two_requests_made_in_the_same_second_do_not_collide() {
        // A patch cycle submits one request per application back to back; colliding names would
        // make one application read another's result.
        let dir = scratch_dir("collision");

        let first = write_request(&dir, RequestKind::AppPatch, "A").unwrap();
        let second = write_request(&dir, RequestKind::AppPatch, "B").unwrap();

        assert_ne!(first, second);
        assert_eq!(fs::read_dir(&dir).unwrap().count(), 2);
    }
}
