mod checkin_schedule;
mod config;
mod dialogs;
mod identity;
mod input_injection;
mod logging;
mod os_update;
mod patch_cycle;
mod policy;
mod progress_window;
mod pty;
mod queue;
mod remote_control;
mod remote_protocol;
mod remote_shell;
mod schedule;
mod screen_capture;
mod self_removal;
mod self_update;
mod status;
mod system_info;
mod tray_menu;
mod upgrade;

use std::collections::HashSet;
use std::sync::mpsc;
use std::time::Duration;

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

use config::Config;
use schedule::ScheduleState;
use status::{AgentStatus, CheckInStatus, MenuAction, StatusReporter, StatusReporterFn};
use system_info::InstalledApp;

/// How often the `--agent` loop wakes to check whether a patch cycle is due. Deliberately not
/// tied to the patching interval itself — this is just the scheduler's own tick rate, small
/// enough that a due time (or a delay elapsing, including one that elapsed while the Mac was
/// asleep — see `ScheduleState::is_due`) is noticed promptly rather than up to a day late.
const AGENT_POLL_INTERVAL: Duration = Duration::from_secs(60);

/// How long a cached patching policy is trusted before the `--agent` loop bothers re-fetching it
/// — the policy changes rarely, so there's no need to hit the server every poll tick.
const POLICY_REFRESH_INTERVAL: u64 = 60 * 60;

/// launchd retries this job on its own schedule (RunAtLoad + hourly
/// StartCalendarInterval); this bounded retry only exists to ride out the
/// short window at boot where the network isn't up yet.
const MAX_ATTEMPTS: u32 = 5;
const INITIAL_BACKOFF: Duration = Duration::from_secs(5);

/// A GET's own retry budget, which is deliberately not the POST one above.
///
/// A POST reports something that has already happened and can afford minutes of backoff; a GET is
/// holding up a patch cycle somebody may be watching. So this takes a per-attempt timeout well
/// under the client's own 15s and a short delay between attempts — a blackholed SYN is not answered
/// by waiting longer, it is answered by a fresh connection. See `get_with_retry`.
const GET_ATTEMPTS: u32 = 3;
const GET_ATTEMPT_TIMEOUT: Duration = Duration::from_secs(10);
const GET_RETRY_DELAY: Duration = Duration::from_millis(500);

#[derive(Debug, Serialize)]
struct RegisterHostRequest {
    hostname: String,
    #[serde(rename = "serialNumber")]
    serial_number: String,
    /// The minute-of-hour (0-59) this host currently checks in on — see `checkin_schedule`. Sent
    /// on every check-in so the server can track load per minute and, in its response, tell this
    /// host to move to a different one if its current minute is carrying more than its share.
    #[serde(rename = "checkInMinute")]
    check_in_minute: u8,
    #[serde(rename = "operatingSystem", skip_serializing_if = "Option::is_none")]
    operating_system: Option<String>,
    #[serde(rename = "ipAddress", skip_serializing_if = "Option::is_none")]
    ip_address: Option<String>,
    #[serde(rename = "operatingSystemUpdateAvailable", skip_serializing_if = "Option::is_none")]
    operating_system_update_available: Option<bool>,
    #[serde(rename = "operatingSystemLatestVersion", skip_serializing_if = "Option::is_none")]
    operating_system_latest_version: Option<String>,
    /// This build's own version, so the Hosts screen can show which agent release each host is
    /// running — mirrors `CreateHostCommand.AgentVersion`. Always sent: the agent always knows it.
    #[serde(rename = "agentVersion")]
    agent_version: &'static str,
}

/// Mirrors the backend's `CreateHostResult` — see
/// Kintsugi.Application/Hosts/Commands/CreateHost/CreateHostCommand.cs. Fields this agent has no
/// use for (host, wasCreated) stay omitted, the same way `self_update`'s `AgentPackageInfo` omits
/// ones it doesn't need.
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RegisterHostResponse {
    #[serde(default)]
    suggested_check_in_minute: Option<u8>,
    /// Set once an admin has requested this host be removed (see
    /// Kintsugi.Domain.Entities.Host.RemovalRequested) — tells this check-in to uninstall the
    /// agent completely instead of continuing on to application registration and everything else
    /// below it. See `self_removal::run`.
    #[serde(default)]
    removal_requested: bool,
}

#[derive(Debug, Serialize)]
struct RegisterApplicationsRequest {
    #[serde(rename = "serialNumber")]
    serial_number: String,
    applications: Vec<InstalledApp>,
}

fn main() -> Result<()> {
    // reqwest's rustls backend needs a process-wide default crypto provider installed before any
    // TLS connection is made; with exactly one provider feature compiled in (ring — see
    // Cargo.toml) higher-level callers usually do this themselves, but installing it explicitly,
    // once, up front removes any doubt — install_default() is a harmless no-op error (ignored
    // here) if something else already installed one first.
    let _ = rustls::crypto::ring::default_provider().install_default();

    // A panic in the scheduler thread would otherwise only ever reach the default panic hook's
    // raw stderr — captured by launchd into /tmp/kintsugi-agent-ui.err.log (see the LaunchAgent
    // plist) for the --agent process, but never into agent.log itself, since a panic bypasses
    // logging::error entirely. Routing it through the same logger means a silent-looking failure
    // (the scheduler thread dying, with the menu bar just never updating again) always leaves a
    // trace in the one file this agent's own docs point people at first.
    std::panic::set_hook(Box::new(|info| logging::error(&format!("panic: {info}"))));

    if std::env::args().any(|arg| arg == "--agent") {
        return run_ui_agent();
    }

    // A root shell session, started by launchd's `WatchPaths` on the remote-shell queue rather than
    // on a schedule. Its own mode and its own job because it runs for as long as somebody is typing:
    // launchd will not run two instances of one job, so sharing the check-in daemon's would stall
    // this host's check-ins for the length of a support call. See `remote_shell`.
    if std::env::args().any(|arg| arg == "--remote-shell") {
        return run_remote_shell();
    }

    run_daemon()
}

/// One invocation of the remote-shell daemon: run whatever sessions are queued, then exit.
///
/// Deliberately does **not** check in, register, or touch the patch queue. It is a second root entry
/// point that exists only to hold a PTY on a socket, and giving it any of the daemon's other work
/// would mean a support call could trigger a patch cycle.
fn run_remote_shell() -> Result<()> {
    // Into the daemon's own log, not the per-user agent's: this runs as root and its lines belong
    // beside the check-in daemon's. Without this call `logging::info` here would reach only the
    // stderr launchd captures, which is the file nobody is pointed at first.
    logging::init(&config::daemon_log_path());

    let config = Config::load();
    let serial_number = system_info::serial_number().context("could not determine serial number")?;

    remote_shell::run(&config, &serial_number)
}

/// What a check-in leaves behind for the schedule step in `run_daemon`.
enum CheckInOutcome {
    /// Registered; the server may have handed back a different minute because this host's is
    /// carrying more load than others.
    Completed { suggested_check_in_minute: Option<u8> },
    /// The server had marked this host for removal and `self_removal` has torn the jobs down —
    /// there is no plist left to schedule.
    Uninstalled,
}

/// The root LaunchDaemon's job: registers this host and its installed applications (as it always
/// has), then drains any pending request left by the `--agent` process — an OS update to install,
/// or an AI-researched application script to run as root — see `queue::process_queue`. Runs once
/// per invocation; launchd re-invokes it at boot and, hourly, at this host's own assigned check-in
/// minute (see `checkin_schedule`) — plus, via the LaunchDaemon's `WatchPaths`, on demand whenever a
/// request appears, since both of those need root and the `--agent` deliberately doesn't have that.
fn run_daemon() -> Result<()> {
    let config = Config::load();
    logging::init(&config::daemon_log_path());
    logging::info(&format!(
        "kintsugi-agent starting; api_base_url={} (config file: {})",
        config.api_base_url,
        config::default_config_path().display()
    ));

    // Before anything that can fail over the network: put right what a self-update cannot. Both of
    // these are repairs rather than installations, and both exist because `self_update` runs under
    // the *old* binary and never re-runs packaging/install.sh — so a host in the field has no other
    // path back to a correct state. The first restores `root:wheel` on the binary launchd executes
    // as root and on `kintsugi-mas`; the second makes sure a terminal session has a job to be
    // served by. Silent on the overwhelming majority of check-ins, where there is nothing to do.
    self_update::repair_installed_ownership();
    remote_shell::ensure_job_installed();

    let checkin_schedule_path = config::checkin_schedule_path();
    let checkin_minute = checkin_schedule::load_or_assign(&checkin_schedule_path);

    let outcome = register_and_report(&config, checkin_minute);

    // Last of all, and whether or not the check-in succeeded: reconcile the on-disk plist with the
    // minute this host should be using — its own already-assigned one, or a different one the
    // server just handed back (see checkin_schedule::apply for why this has to be the very last
    // thing a check-in does). The failure path matters as much as the success path. The packaged
    // plist carries no `StartCalendarInterval` (see packaging/au.com.sharpblue.kintsugiagent.plist),
    // only `RunAtLoad` and `WatchPaths`, so until this writes one the daemon has no hourly schedule
    // at all — and a first run that failed (a blank enrollment token, a server that was down) used
    // to return before reaching this point, leaving the host with nothing to retry it until a
    // reboot, a queue request or a human ran `launchctl kickstart`. The one exception is a host the
    // server has told to uninstall: `self_removal` has just deleted the plist, and rewriting it
    // would resurrect the schedule for a binary that is no longer there.
    let target_minute = match &outcome {
        Ok(CheckInOutcome::Uninstalled) => return Ok(()),
        Ok(CheckInOutcome::Completed { suggested_check_in_minute }) => {
            suggested_check_in_minute.unwrap_or(checkin_minute)
        }
        Err(_) => checkin_minute,
    };
    checkin_schedule::apply(&checkin_schedule_path, target_minute);

    outcome.map(|_| ())
}

/// The body of a check-in — everything between reading this host's check-in minute and applying
/// it to the plist, split out so `run_daemon` can run the schedule step on every exit path.
fn register_and_report(config: &Config, checkin_minute: u8) -> Result<CheckInOutcome> {
    let hostname = system_info::hostname().context("could not determine hostname")?;
    let serial_number = system_info::serial_number().context("could not determine serial number")?;

    // Best-effort: registration still proceeds with just hostname + serial
    // number if either of these can't be determined.
    let operating_system = system_info::operating_system()
        .inspect_err(|err| logging::warn(&format!("could not determine operating system: {err}")))
        .ok();
    let ip_address = system_info::local_ip_address()
        .inspect_err(|err| logging::warn(&format!("could not determine local IP address: {err}")))
        .ok();

    // Best-effort, same as the OS name/version above: a host that can't run the standard update
    // check for some reason still gets registered, just without this piece reported.
    let os_update_status = os_update::check()
        .inspect_err(|err| logging::warn(&format!("could not check for macOS updates: {err}")))
        .ok();

    logging::info(&format!(
        "registering host: hostname={hostname} serial_number={serial_number} operating_system={operating_system:?} ip_address={ip_address:?} os_update_status={os_update_status:?}"
    ));

    // Every request below needs to authenticate as this host — see nginx/default.conf, which
    // rejects /api/host, /api/applications, /api/patching-policy, and /api/upgrade-paths outright
    // without a valid client certificate. Enrolls on first run; reuses the same identity from then
    // on, until it needs replacing (e.g. this host was decommissioned and re-provisioned).
    let agent_identity = identity::load_or_enroll(config, &serial_number);
    let client = identity::build_client(Duration::from_secs(15), agent_identity.as_ref())
        .context("failed to build HTTP client")?;

    let host_request = RegisterHostRequest {
        hostname,
        serial_number: serial_number.clone(),
        check_in_minute: checkin_minute,
        operating_system,
        ip_address,
        operating_system_update_available: os_update_status.as_ref().map(|s| s.available),
        operating_system_latest_version: os_update_status.and_then(|s| s.latest_version),
        agent_version: env!("CARGO_PKG_VERSION"),
    };
    let host_response: RegisterHostResponse = post_with_retry(&client, &config.register_host_url(), &host_request)
        .context("failed to register host")?;

    if host_response.removal_requested {
        logging::info("the server has marked this host for removal — uninstalling instead of continuing this check-in");
        self_removal::run(&client, config, &serial_number);
        return Ok(CheckInOutcome::Uninstalled);
    }

    let applications = collect_installed_applications();
    logging::info(&format!("reporting {} installed application(s)", applications.len()));

    let applications_request = RegisterApplicationsRequest {
        serial_number: serial_number.clone(),
        applications,
    };
    let _: serde_json::Value = post_with_retry(&client, &config.register_applications_url(), &applications_request)
        .context("failed to register installed applications")?;

    // The privileged steps the (non-root) `--agent` process hands off here: installing a pending
    // macOS software update, and running an AI-researched application's upgrade script, whose
    // target in /Applications is routinely root-owned — see `queue`. Cheap to check on every
    // invocation — normally a no-op, since `WatchPaths` (see the LaunchDaemon plist) is what
    // actually wakes this daemon promptly when a request is dropped, rather than this being polled
    // on a schedule.
    queue::process_queue(
        &config::queue_dir(),
        &mut DaemonRequestHandler {
            client: &client,
            config,
            serial_number: &serial_number,
            identity: agent_identity.as_ref(),
        },
    );

    // Last, and only after everything above has already succeeded: check whether a newer build of
    // this agent itself has been published, and install it in place if so — see `self_update`.
    // Runs on every check-in (RunAtLoad + hourly + on-demand), the same cadence as registration
    // itself, since there's no separate patching policy governing the agent's own updates.
    self_update::check_and_apply(&client, config, agent_identity.as_ref(), env!("CARGO_PKG_VERSION"));

    Ok(CheckInOutcome::Completed {
        suggested_check_in_minute: host_response.suggested_check_in_minute,
    })
}

/// The daemon's answers to the per-user process's requests — see `queue`. Holds what the requests
/// deliberately do not carry: the authenticated client, and this host's identity with the pinned
/// artifact-signing key every script is verified against.
struct DaemonRequestHandler<'a> {
    client: &'a reqwest::blocking::Client,
    config: &'a Config,
    serial_number: &'a str,
    identity: Option<&'a identity::AgentIdentity>,
}

impl queue::RequestHandler for DaemonRequestHandler<'_> {
    /// Runs one application's upgrade as root.
    ///
    /// The work list is re-fetched from the server here rather than trusted from the request, which
    /// is the property that makes the queue safe: the request named an application, and everything
    /// actually executed — the script, its signature, the identifier it's addressed by — comes from
    /// the server and is verified against the pinned artifact-signing key. A request that names an
    /// application with no signed, patchable upgrade path simply fails. So does one naming a row
    /// `upgrade::runs_as_root` says belongs to the logged-in user: the per-user process never asks
    /// for those, and Homebrew must not be run as root on anybody's say-so. (An App Store row is
    /// root's — see `runs_as_root` for why — so it passes this gate and its script does the
    /// `launchctl asuser` work itself.) Same shape as the Windows service's `patch_application`.
    fn patch_application(&mut self, application_name: &str) -> Result<()> {
        let identity = self.identity.context("this agent has not enrolled an identity yet")?;

        let status = upgrade::fetch_upgrade_statuses(self.client, self.config, self.serial_number)?
            .into_iter()
            .filter(|status| upgrade::is_patchable(status, identity))
            .find(|status| status.application_name.eq_ignore_ascii_case(application_name))
            .with_context(|| format!("'{application_name}' has no signed, patchable upgrade path"))?;

        if !upgrade::runs_as_root(&status) {
            anyhow::bail!(
                "'{}' is managed by {} and runs as the logged-in user, not as root — refusing",
                status.application_name,
                status.package_manager.as_deref().unwrap_or("a package-manager command")
            );
        }

        logging::info(&format!("attempting to patch {} (method {:?}) as root", status.application_name, status.method));
        if let Err(err) = upgrade::patch_one(&status, identity) {
            // Reported from here rather than by the per-user process, for the same reason the
            // success below is: this is the side that ran the script and saw its output. The
            // per-user process deliberately skips reporting anything that came back through
            // `queue`, so a root-run failure is recorded exactly once.
            //
            // Note what sits *above* this line and so is never reported: no enrolled identity, no
            // signed patchable path, and the `runs_as_root` refusal. Those are configuration
            // problems rather than bugs in a script, and the Failed Updates screen exists for the
            // ones a human or the AI can fix.
            upgrade::report_patch_failure(self.client, self.config, self.serial_number, &status, &err);
            return Err(err);
        }
        logging::info(&format!("patched {} successfully", status.application_name));

        match &status.latest_version {
            Some(new_version) => {
                upgrade::report_patch_result(self.client, self.config, self.serial_number, &status.application_name, new_version)
            }
            None => logging::warn(&format!(
                "patched {} successfully, but no latest_version was known to report to the server",
                status.application_name
            )),
        }

        Ok(())
    }

    fn install_os_updates(&mut self) -> Result<()> {
        os_update::install()?;
        // Reported from here rather than by the per-user process, the same as the patch result
        // above: this is the side that knows the install finished.
        os_update::report_patched(self.client, self.config, self.serial_number);
        Ok(())
    }

    /// Answers a [`queue::RequestKind::CheckIn`]. There is nothing left to do: this invocation of
    /// the daemon was started by `WatchPaths` when the request landed, and `run_daemon` registered
    /// the host and reported the inventory before it reached the queue. The agent's own update check
    /// follows the queue drain — see `checkin_schedule::request_now` for why that ordering is what
    /// the per-user process expects.
    fn check_in(&mut self) -> Result<String> {
        Ok(format!("checked in as {} (agent {})", self.serial_number, env!("CARGO_PKG_VERSION")))
    }
}

/// The per-user LaunchAgent's job (`--agent`): runs continuously in the logged-in user's own
/// session — not root, so it can show dialogs/notifications directly, no privilege trickery
/// needed — tracking the fleet-wide patching policy and driving the confirm/delay/patch flow
/// once it's due, plus the menu bar icon (progress / next check-in / next due / Check In Now /
/// Patch Now).
///
/// Splits into two threads because of a hard platform requirement: on macOS, any UI — including
/// just a menu bar status item — must live on the main thread and be driven by a running Cocoa
/// event loop, but the scheduler needs to block on HTTP calls, 5-minute warnings, and `osascript`
/// dialogs. So the *scheduler* runs on a background thread (this function's original loop,
/// unchanged in spirit), and the *main* thread runs winit's event loop hosting the tray icon.
/// They talk to each other one direction each: the scheduler pushes `AgentStatus` and
/// `CheckInStatus` updates to the menu (`report`, ultimately a winit `EventLoopProxy`), and a
/// click on "Check In Now" or "Patch Now" sends a `MenuAction` back to the scheduler over
/// `menu_rx` — see `tray_menu` and `status`.
fn run_ui_agent() -> Result<()> {
    let config = Config::load();
    let state_dir = config::user_state_dir()?;
    logging::init(&state_dir.join("agent.log"));
    logging::info(&format!("kintsugi-agent (--agent) starting; api_base_url={} state_dir={}", config.api_base_url, state_dir.display()));

    let serial_number = system_info::serial_number().context("could not determine serial number")?;

    // Reads the identity the root daemon already enrolled (see run_daemon) — this process never
    // enrolls one itself, since it deliberately doesn't run as root and enrollment's whole point
    // is establishing an identity for the *host*, not a second one for whichever user is logged
    // in. If the daemon hasn't enrolled yet (e.g. very first boot, no enrollment token configured
    // yet), every request below simply gets rejected by nginx until it has — see
    // identity::load_or_enroll's own logging for why.
    let agent_identity = identity::load(&config::identity_dir());
    if agent_identity.is_none() {
        logging::warn("no enrolled agent identity found yet — requests will be rejected until the root daemon enrolls one");
    }
    let client = identity::build_client(Duration::from_secs(30), agent_identity.as_ref())
        .context("failed to build HTTP client")?;

    let policy_cache_path = state_dir.join("policy.json");
    let schedule_state_path = state_dir.join("schedule.json");

    // Block (retrying) until a policy is available at all — nothing meaningful can be scheduled
    // without one, and this only ever happens once, at first-ever startup with no cache and no
    // network yet (e.g. very early in boot).
    let current_policy = loop {
        if let Some(policy) = policy::load_or_fetch(&client, &config, &policy_cache_path) {
            break policy;
        }
        std::thread::sleep(AGENT_POLL_INTERVAL);
    };

    let state = ScheduleState::load_or_default(&schedule_state_path, &current_policy);

    let (menu_tx, menu_rx) = mpsc::channel();
    let report: StatusReporterFn = tray_menu::report_status;

    // Set by the menu bar's "End Remote Session" and cleared by whoever acts on it. Shared rather
    // than a channel because the click has to be meaningful whether or not a session is running at
    // that instant — see tray_menu's handler.
    let end_remote_session = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));

    // A third thread, and the reason it is separate from the scheduler: it spends its whole life
    // blocked on a socket, whereas the scheduler spends its whole life asleep on a timer. Sharing
    // one would mean a session request waiting up to a minute for the next tick, which defeats the
    // point of holding a socket at all.
    //
    // This is the only part of this agent that holds a *standing* connection to the server.
    // Everything else — check-in, policy, patch results — is a request the agent makes when it has
    // something to say. Remote control is the one case where the server needs to reach the host,
    // and an hourly check-in cannot carry "somebody would like to see your screen now".
    let remote_control_config = config.clone();
    let remote_control_serial = serial_number.clone();
    let remote_control_flag = end_remote_session.clone();
    std::thread::spawn(move || remote_control::run(remote_control_config, remote_control_serial, remote_control_flag));

    std::thread::spawn(move || {
        run_scheduler(client, config, current_policy, state, schedule_state_path, serial_number, agent_identity, policy_cache_path, menu_rx, report)
    });

    // Blocks for the rest of the process's life — this call never returns normally.
    tray_menu::run(menu_tx, end_remote_session)
}


/// How a patch cycle is started — `patch_cycle::run` for a naturally due one, `run_now` for a
/// "Patch Now" click. Their signatures are identical, so `spawn_cycle` is handed whichever one is
/// meant rather than a flag to branch on.
type CycleFn = fn(
    &reqwest::blocking::Client,
    &Config,
    &policy::PatchingPolicy,
    &mut ScheduleState,
    &str,
    &identity::AgentIdentity,
    &StatusReporter,
);

/// Runs one patch cycle on its own thread, handing it the schedule state for the duration and
/// getting it back when it finishes.
///
/// A thread rather than a plain call, because the confirmation dialog stands there for a whole
/// delay period and the scheduler used to be parked inside it for all of it: the "Next check-in"
/// line stopped updating, and a menu click sat in the channel until the dialog came down, which
/// from the menu bar looks like the item did nothing. (The Linux agent had a third and worse
/// symptom — see its own copy of this comment.)
///
/// The state goes *with* the cycle rather than being shared behind a lock: a mutex held for the
/// hours a dialog can stand there would have moved the block rather than removed it, since
/// `is_due` would then be the thing waiting. So `state` being `None` in `run_scheduler`'s loop is
/// exactly "a cycle is in flight", and it is what stops a second one starting.
///
/// Everything else is cheap to hand over — the `Client` shares its connection pool with the
/// clone, `report` is a function pointer — and each cycle works from the policy and identity as
/// they were when it started. That is no different from before: the loop's re-reads only ever
/// affected the *next* cycle, and neither a policy nor a certificate changes mid-cycle in a way
/// this one would want to act on.
fn spawn_cycle(
    cycle: CycleFn,
    client: &reqwest::blocking::Client,
    config: &Config,
    policy: &policy::PatchingPolicy,
    mut state: ScheduleState,
    serial_number: &str,
    identity: &identity::AgentIdentity,
    report: StatusReporterFn,
) -> std::thread::JoinHandle<ScheduleState> {
    let (client, config, policy) = (client.clone(), config.clone(), policy.clone());
    let (serial_number, identity) = (serial_number.to_string(), identity.clone());
    std::thread::spawn(move || {
        cycle(&client, &config, &policy, &mut state, &serial_number, &identity, &report);
        state
    })
}

/// The background half of `run_ui_agent` — see its doc comment for why this is a separate
/// thread. Reports its state to the menu bar via `report` at every meaningful transition, and
/// treats a "Patch Now" click the same as a naturally due cycle except it skips the confirm/delay
/// step entirely (see `patch_cycle::run_now`). A "Check In Now" click goes to the root daemon
/// through the queue (see `checkin_schedule::request_now`).
fn run_scheduler(
    client: reqwest::blocking::Client,
    config: Config,
    mut current_policy: policy::PatchingPolicy,
    state: ScheduleState,
    schedule_state_path: std::path::PathBuf,
    serial_number: String,
    // Whatever `identity::load` found (or didn't) at process startup — this process never enrolls
    // one itself (see run_ui_agent's own comment on that), but it's not necessarily *permanently*
    // unenrolled either: the root daemon enrolls independently and asynchronously, and this
    // per-user process is long-running (KeepAlive), so it can easily already be up and running
    // from before the daemon ever got there (first boot, a delayed enrollment token, ...). So
    // when this is `None`, the loop below re-checks disk on every tick rather than giving up for
    // the rest of this process's life — cheap (a few local file reads, no network) next to the
    // alternative of the menu bar silently refusing to work until someone thinks to restart it.
    mut agent_identity: Option<identity::AgentIdentity>,
    policy_cache_path: std::path::PathBuf,
    menu_rx: mpsc::Receiver<MenuAction>,
    report: StatusReporterFn,
) {
    report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });

    // `None` for exactly as long as a cycle owns the schedule state — see `spawn_cycle`, which is
    // also why this loop can be sure it is never running two.
    let mut state = Some(state);
    let mut in_flight: Option<std::thread::JoinHandle<ScheduleState>> = None;

    // The daemon's schedule, as last shown in the menu. Re-read every tick — the daemon persists a
    // minute on its first run and the server may move it on any check-in — but only pushed to the
    // menu when the answer changes, which is once an hour: `tray_menu::format_due` shells out to
    // `date`, and there is no reason to do that once a minute for a line that has not moved.
    let checkin_schedule_path = config::checkin_schedule_path();
    let mut shown_check_in: Option<CheckInStatus> = None;

    loop {
        // Takes the schedule state back from a cycle that has finished, and says so. Reporting
        // here rather than trusting the cycle to is what covers its early returns — an
        // unreachable server, nothing to patch, a dialog that would not launch — since those
        // report nothing and would otherwise leave the menu greyed on "waiting for your answer"
        // for a prompt that is no longer there.
        if in_flight.as_ref().is_some_and(|handle| handle.is_finished()) {
            match in_flight.take().expect("just checked that a handle is there").join() {
                Ok(finished) => state = Some(finished),
                Err(_) => {
                    // Nothing in a cycle is expected to panic, but a scheduler that quietly
                    // stopped scheduling would be the worst possible way to find out: reload from
                    // disk (the state is saved on every change, so disk is the freshest copy) and
                    // carry on, so a repeating panic shows up as a repeating log line rather than
                    // as a host that silently never patches again.
                    logging::error("the patch cycle thread panicked; reloading the schedule from disk and carrying on");
                    state = Some(ScheduleState::load_or_default(&schedule_state_path, &current_policy));
                }
            }
            // Reported only when the cycle is over *and* nothing is pending. An unanswered
            // dialog deliberately leaves the state due right now (see
            // `ScheduleState::register_unanswered_prompt`), so the next tick re-asks within the
            // minute — and announcing "next patch due: <a moment ago>" with both actions enabled
            // in between would flicker the menu once per delay period for the whole budget.
            if let Some(current) = state.as_ref().filter(|current| !current.is_due()) {
                report(AgentStatus::Idle { next_due_epoch: current.next_due_epoch() });
            }
        }

        if agent_identity.is_none() {
            agent_identity = identity::load(&config::identity_dir());
            if agent_identity.is_some() {
                logging::info("agent identity now available (the root daemon must have enrolled since this process started)");
            }
        }

        if policy::is_stale(&current_policy, POLICY_REFRESH_INTERVAL) {
            if let Some(refreshed) = policy::load_or_fetch(&client, &config, &policy_cache_path) {
                current_policy = refreshed;
            }
        }

        let next_check_in = CheckInStatus::Scheduled { next_epoch: checkin_schedule::next_check_in_epoch(&checkin_schedule_path) };
        if shown_check_in != Some(next_check_in) {
            tray_menu::report_check_in(next_check_in);
            shown_check_in = Some(next_check_in);
        }

        // Waits on the channel rather than sleeping and polling it once per iteration — a click
        // in the menu bar wakes this immediately instead of sitting unnoticed for up to
        // AGENT_POLL_INTERVAL, which from the menu bar just looks like the button did nothing.
        match menu_rx.recv_timeout(AGENT_POLL_INTERVAL) {
            Ok(MenuAction::PatchNow) => {
                logging::info("scheduler received the Patch Now signal");
                // Every branch that declines has to say so out loud: unlike a naturally-due cycle
                // finding nothing to do (silent by design — see patch_cycle::run), this is an
                // explicit action the user just took, so it must never look like nothing happened
                // even when there's a real reason it can't proceed.
                match (state.take(), &agent_identity) {
                    (Some(owned), Some(identity)) => {
                        in_flight = Some(spawn_cycle(
                            patch_cycle::run_now,
                            &client,
                            &config,
                            &current_policy,
                            owned,
                            &serial_number,
                            identity,
                            report,
                        ));
                    }
                    (Some(owned), None) => {
                        state = Some(owned);
                        logging::warn("Patch Now ignored: no enrolled agent identity yet");
                        dialogs::notify("Kintsugi Patching", "Not yet enrolled with the server — try again shortly.");
                    }
                    // The menu greys both actions whenever this can happen, so getting here means
                    // a click was already on its way.
                    (None, _) => {
                        logging::info("Patch Now ignored: a patch cycle is already running");
                        dialogs::notify("Kintsugi Patching", "A patch cycle is already running.");
                    }
                }
            }
            Ok(MenuAction::CheckInNow) => {
                logging::info("scheduler received the Check In Now signal");
                tray_menu::report_check_in(CheckInStatus::InProgress);
                checkin_schedule::request_now(&config::queue_dir());
                // Forces the schedule line back at the top of the next tick, whatever it now says
                // — the server may just have moved this host's minute.
                shown_check_in = None;
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {
                if state.as_ref().is_some_and(|current| current.is_due()) {
                    match &agent_identity {
                        Some(identity) => {
                            let owned = state.take().expect("just checked that the state is here");
                            in_flight = Some(spawn_cycle(
                                patch_cycle::run,
                                &client,
                                &config,
                                &current_policy,
                                owned,
                                &serial_number,
                                identity,
                                report,
                            ));
                        }
                        None => logging::warn("patch cycle due, but skipped: no enrolled agent identity yet"),
                    }
                }
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                // The sender lives in tray_menu::run for the whole life of the process, so this
                // should never happen — but if it does, fall back to plain polling rather than
                // spin-looping on an instantly-erroring recv.
                logging::error("menu action channel disconnected unexpectedly");
                std::thread::sleep(AGENT_POLL_INTERVAL);
            }
        }
    }
}

/// Combines /Applications bundle scanning with Homebrew formulae/casks. A
/// cask-installed GUI app also lives under /Applications, so the folder
/// scan is told which bundle names Homebrew already accounts for
/// (`cask_app_bundle_names`) and skips those, keeping the Homebrew-tagged
/// entry as the single source of truth for that app rather than also
/// reporting it as a separate, unmanaged application. The folder scan itself
/// tells App Store installs apart from standalone bundles by their receipt
/// (see `system_info::read_app_bundle`). Any remaining exact
/// (name, version, ...) duplicates are still deduplicated, since the
/// backend rejects duplicate (host, name, version) rows in a single report.
fn collect_installed_applications() -> Vec<InstalledApp> {
    let homebrew = system_info::scan_homebrew();

    let mut seen = HashSet::new();
    system_info::scan_applications_folder(&homebrew.cask_app_bundle_names)
        .into_iter()
        .chain(homebrew.apps)
        .filter(|app| seen.insert(app.clone()))
        .collect()
}

/// A GET that survives the moment of packet loss a POST already survives.
///
/// `post_with_retry` has retried since this agent was written, so an intermittently lossy path to
/// the server is invisible on every POST — enrollment, inventory, patch results — and was fatal on
/// every GET, each of which had exactly one attempt. On one host that cost a whole patch cycle to a
/// bad minute: `upgrade::fetch_upgrade_statuses` is called once per application, deliberately, so
/// five applications in a row each spent the client's full timeout and reported "operation timed
/// out". The same link on the same evening is what `remote_control`'s session-socket retry exists
/// for; this is the third place the asymmetry showed up, and the two halves now sit together so a
/// fourth is harder to write.
///
/// The response is returned whatever its status. Every caller already tells a rejection apart from
/// a failure to reach the server, and a 4xx is not going to fix itself on retry.
pub(crate) fn get_with_retry(
    description: &str,
    build: impl Fn() -> reqwest::blocking::RequestBuilder,
) -> Result<reqwest::blocking::Response> {
    let mut last_error = None;

    for attempt in 1..=GET_ATTEMPTS {
        match build().timeout(GET_ATTEMPT_TIMEOUT).send() {
            Ok(response) => return Ok(response),
            Err(err) => {
                // {err:#} for the cause chain, the same reason post_with_retry does it.
                logging::warn(&format!("attempt {attempt}/{GET_ATTEMPTS} to fetch {description} failed: {err:#}"));
                last_error = Some(err);
            }
        }

        if attempt < GET_ATTEMPTS {
            std::thread::sleep(GET_RETRY_DELAY);
        }
    }

    Err(anyhow::anyhow!(
        "could not fetch {description} after {GET_ATTEMPTS} attempts: {}",
        last_error.map(|err| err.to_string()).unwrap_or_default()
    ))
}

fn post_with_retry<T: Serialize, R: serde::de::DeserializeOwned>(client: &reqwest::blocking::Client, url: &str, body: &T) -> Result<R> {
    let mut backoff = INITIAL_BACKOFF;
    let mut last_error = None;

    for attempt in 1..=MAX_ATTEMPTS {
        match client.post(url).json(body).send() {
            Ok(response) if response.status().is_success() => {
                let status = response.status();
                let body = response.text().unwrap_or_default();
                logging::info(&format!("POST {url} succeeded (HTTP {status}): {body}"));
                return serde_json::from_str(&body).context("could not parse response body");
            }
            Ok(response) => {
                let status = response.status();
                let body = response.text().unwrap_or_default();
                // A 4xx is not going to fix itself on retry (bad payload,
                // validation failure); fail fast instead of burning the
                // retry budget.
                anyhow::bail!("request rejected (HTTP {status}): {body}");
            }
            Err(err) => {
                // {err:#}, not {err}: anyhow's plain Display prints only the outermost
                // message, and reqwest's outermost message for any connection failure is the
                // bare "error sending request for url (...)" — identical whether the host is
                // unreachable, the TLS handshake was rejected, or DNS failed. The cause chain is
                // where "invalid peer certificate: UnknownIssuer" lives, and without it a server
                // presenting an untrusted certificate is indistinguishable in this log from a
                // network outage. That cost real time to diagnose once; don't drop the `#`.
                logging::warn(&format!("attempt {attempt}/{MAX_ATTEMPTS} to {url} failed: {err:#}"));
                last_error = Some(err);
            }
        }

        if attempt < MAX_ATTEMPTS {
            std::thread::sleep(backoff);
            backoff *= 2;
        }
    }

    Err(anyhow::anyhow!(
        "failed after {MAX_ATTEMPTS} attempts: {}",
        last_error.map(|e| e.to_string()).unwrap_or_default()
    ))
}
