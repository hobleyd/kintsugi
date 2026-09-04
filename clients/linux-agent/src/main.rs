mod checkin_schedule;
mod config;
mod dialogs;
mod identity;
mod input_injection;
mod lock;
mod logging;
mod os_update;
mod patch_cycle;
mod policy;
mod progress_window;
mod queue;
mod backend;
mod remote_control;
mod remote_ipc;
mod remote_protocol;
mod remote_session;
mod wayland_backend;
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
use identity::AgentIdentity;
use queue::{Plan, PlannedApp, RequestHandler, RequestKind};
use schedule::ScheduleState;
use status::{AgentStatus, StatusReporter};
use system_info::InstalledApp;

/// How often the `--agent` loop wakes to check whether a patch cycle is due. Deliberately not
/// tied to the patching interval itself — this is just the scheduler's own tick rate, small
/// enough that a due time (or a delay elapsing, including one that elapsed while the host was
/// suspended — see `ScheduleState::is_due`) is noticed promptly rather than up to a day late.
const AGENT_POLL_INTERVAL: Duration = Duration::from_secs(60);

/// systemd retries this unit on its own schedule (an hourly timer plus `OnBootSec`); this bounded
/// retry only exists to ride out the short window at boot where the network isn't up yet.
const MAX_ATTEMPTS: u32 = 5;
const INITIAL_BACKOFF: Duration = Duration::from_secs(5);

/// How long a root invocation waits for another one to finish before giving up — see `lock`. The
/// timer fires hourly, so anything that has to be skipped here is picked back up soon enough.
const PRIVILEGED_LOCK_TIMEOUT: Duration = Duration::from_secs(60);

/// Queue round-trip budgets, from the per-user process's point of view. A plan is one HTTPS call
/// made by an already-running service; an application patch is a package download and install; an
/// OS update is a whole distribution's worth of packages, which on a host that has been switched
/// off for a month genuinely can take hours.
const PLAN_TIMEOUT: Duration = Duration::from_secs(2 * 60);
const APP_PATCH_TIMEOUT: Duration = Duration::from_secs(60 * 60);
const OS_UPDATE_TIMEOUT: Duration = Duration::from_secs(3 * 60 * 60);

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
    // raw stderr — captured by systemd into the journal, but never into agent.log itself, since a
    // panic bypasses logging::error entirely. Routing it through the same logger means a
    // silent-looking failure (the scheduler thread dying, with the menu just never updating
    // again) always leaves a trace in the one file this agent's own docs point people at first.
    std::panic::set_hook(Box::new(|info| logging::error(&format!("panic: {info}"))));

    let args: Vec<String> = std::env::args().collect();

    if args.iter().any(|arg| arg == "--agent") {
        return run_ui_agent();
    }

    if args.iter().any(|arg| arg == "--process-queue") {
        return run_queue_service();
    }

    if args.iter().any(|arg| arg == "--remote-control") {
        return run_remote_control_service();
    }

    run_daemon()
}

/// The resident root unit (`kintsugi-agent-remote.service`): holds the remote control sockets.
///
/// The fourth root entry point and the only long-running one. The other three cannot hold a standing
/// connection — two are oneshots and the third is the per-user process, which has no identity — and
/// remote control needs one, because the server has to reach the host within seconds rather than at
/// the next hourly check-in.
///
/// **It takes no lock.** `lock.rs` exists so a queue-triggered patch cannot land inside an
/// unattended cycle and deadlock two `apt-get` runs on the dpkg lock. This unit installs nothing, so
/// holding that lock would only mean a remote session blocked patching for as long as somebody was
/// watching.
fn run_remote_control_service() -> Result<()> {
    let config = Config::load();
    logging::init(&config::daemon_log_path());

    let serial_number = system_info::serial_number().context("could not determine this machine's identifier")?;

    logging::info(&format!(
        "kintsugi-agent (--remote-control) starting; api_base_url={} serial_number={serial_number}",
        config.api_base_url
    ));

    // Never returns. systemd restarts the unit if it does.
    remote_control::run(config, serial_number);

    Ok(())
}

/// The root service's job (`kintsugi-agent.service`, driven by `kintsugi-agent.timer`): registers
/// this host and its installed applications, patches unattended if nobody is logged in to be
/// asked, updates this agent itself, and finally reconciles its own check-in schedule. Runs once
/// per invocation; systemd re-invokes it shortly after boot and, hourly, at this host's own
/// assigned check-in minute (see `checkin_schedule`).
fn run_daemon() -> Result<()> {
    let config = Config::load();
    logging::init(&config::daemon_log_path());
    logging::info(&format!(
        "kintsugi-agent starting; api_base_url={} (config file: {})",
        config.api_base_url,
        config::default_config_path().display()
    ));

    // Held for the whole invocation — see `lock` for what it's held against. Nothing below this
    // point may run concurrently with a queue-triggered patch.
    let Some(_privileged) = lock::acquire(PRIVILEGED_LOCK_TIMEOUT) else {
        logging::info("another kintsugi-agent invocation is already running; skipping this check-in");
        return Ok(());
    };

    // Before anything else that depends on them: re-assert the two directory modes the privilege
    // handoff rests on. `self_update` replaces the binary and never re-runs the installer, so a
    // host that received the wrong modes from packaging/install.sh would otherwise keep them
    // forever — see `config::repair_directory_modes`.
    config::repair_directory_modes();

    let checkin_schedule_path = config::checkin_schedule_path();
    let checkin_minute = checkin_schedule::load_or_assign(&checkin_schedule_path);

    let hostname = system_info::hostname().context("could not determine hostname")?;
    // Deliberately fatal, unlike everything else below: without a unique identity this host would
    // share a certificate — and so a host record, and so its data — with every other host whose
    // firmware ships the same placeholder. See `system_info::serial_number`.
    let serial_number = system_info::serial_number().context("could not determine a unique identity for this host")?;

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
        .inspect_err(|err| logging::warn(&format!("could not check for OS updates: {err}")))
        .ok();

    logging::info(&format!(
        "registering host: hostname={hostname} serial_number={serial_number} operating_system={operating_system:?} ip_address={ip_address:?} os_update_status={os_update_status:?}"
    ));

    // Every request below needs to authenticate as this host — see nginx/default.conf, which
    // rejects /api/host, /api/applications, /api/patching-policy, and /api/upgrade-paths outright
    // without a valid client certificate. Enrolls on first run; reuses the same identity from then
    // on, until it needs replacing (e.g. this host was decommissioned and re-provisioned).
    let agent_identity = identity::load_or_enroll(&config, &serial_number);
    let client = identity::build_client(Duration::from_secs(15), agent_identity.as_ref())
        .context("failed to build HTTP client")?;

    // Deliberately before the registration below, and unconditional: this write is the *only* way
    // the per-user process ever obtains a patching policy (see `config::policy_cache_path`), so
    // anything that could fail between here and there — a registration that 4xxs, an inventory the
    // server rejects — would starve that process of a schedule for as long as the failure lasted,
    // while the host went on reporting healthy check-ins. Cheap enough to be unconditional: one
    // GET per hourly invocation.
    let patching_policy = policy::refresh(&client, &config, &config::policy_cache_path());

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
        self_removal::run(&client, &config, &serial_number);
        return Ok(());
    }

    let applications = collect_installed_applications();
    logging::info(&format!("reporting {} installed application(s)", applications.len()));

    let applications_request = RegisterApplicationsRequest {
        serial_number: serial_number.clone(),
        applications,
    };
    let _: serde_json::Value = post_with_retry(&client, &config.register_applications_url(), &applications_request)
        .context("failed to register installed applications")?;

    patch_unattended_if_nobody_is_logged_in(&client, &config, &serial_number, agent_identity.as_ref(), patching_policy.as_ref());

    // Last, and only after everything above has already succeeded: check whether a newer build of
    // this agent itself has been published, and install it in place if so — see `self_update`.
    // Runs on every check-in, the same cadence as registration itself, since there's no separate
    // patching policy governing the agent's own updates.
    self_update::check_and_apply(&client, &config, agent_identity.as_ref(), env!("CARGO_PKG_VERSION"));

    // Last of all: reconcile the on-disk check-in schedule with whatever minute this host should
    // now be using — its own already-assigned one, or a different one the server just handed back
    // in host_response because this minute is carrying more load than others.
    let target_minute = host_response.suggested_check_in_minute.unwrap_or(checkin_minute);
    checkin_schedule::apply(&checkin_schedule_path, target_minute);

    Ok(())
}

/// Drives a patch cycle from the root service when — and only when — no per-user agent has
/// recently claimed this host.
///
/// This is the one piece of this agent with no counterpart on either other platform, and
/// `patch_cycle::run_unattended` explains why it has to exist: a Linux fleet is mostly servers,
/// and a server has no logged-in user to own the schedule. On a desktop this does nothing at all,
/// and the per-user process runs exactly the flow the macOS and Windows agents run.
fn patch_unattended_if_nobody_is_logged_in(
    client: &reqwest::blocking::Client,
    config: &Config,
    serial_number: &str,
    agent_identity: Option<&AgentIdentity>,
    policy: Option<&policy::PatchingPolicy>,
) {
    let queue_dir = config::queue_dir();
    // Named rather than merely noted: deferring is the one decision here that can leave a host
    // permanently unpatched, and it is not an error, so nothing else in the log marks it. Saying
    // *which* session is holding the schedule and *how stale* its claim is turns "nothing
    // happened" into something an administrator can check against `systemctl --user status`.
    if let Some(heartbeat) = queue::live_ui_agent(&queue_dir, queue::HEARTBEAT_MAX_AGE) {
        logging::info(&format!(
            "a per-user agent is running on this host (uid {}, last heartbeat {}s ago); leaving the patching schedule to it",
            heartbeat.uid,
            heartbeat.age.as_secs()
        ));
        return;
    }

    let Some(identity) = agent_identity else {
        logging::info("skipping the unattended patch cycle: no enrolled agent identity yet");
        return;
    };

    // Fetched by the caller, before registration, because the same file is what the per-user
    // process reads — see `run_daemon`. `None` here means neither this check-in's fetch nor any
    // cached copy produced one.
    let Some(policy) = policy else {
        logging::warn("skipping the unattended patch cycle: no patching policy is available yet");
        return;
    };

    // The schedule state stays the root service's own, separate from any user's: they describe
    // different things (this host, versus one person's session) and must not interfere.
    let mut state = ScheduleState::load_or_default(&config::state_dir().join("schedule.json"), policy);
    let mut handler = ServiceHandler::new(client, config, serial_number, identity);

    patch_cycle::run_unattended(&mut handler, policy, &mut state);
}

/// The queue-draining service's job (`kintsugi-agent-queue.service`, triggered by
/// `kintsugi-agent-queue.path` the moment a request file appears). Deliberately *not* the same
/// unit as the check-in above: a per-user process asks for one application at a time, and running
/// a whole registration pass per application would make patching a dozen applications a dozen
/// full check-ins.
///
/// This is where the macOS agent's `WatchPaths` idea ends up. There, the path watch re-runs the
/// entire daemon, which is affordable because the queue only ever carries one kind of request and
/// only once per cycle.
fn run_queue_service() -> Result<()> {
    let config = Config::load();
    logging::init(&config::daemon_log_path());

    let Some(_privileged) = lock::acquire(PRIVILEGED_LOCK_TIMEOUT) else {
        // Not an error, and not a dropped request either: the path unit re-triggers as long as a
        // request file is still there, so whatever is waiting gets served on the next pass.
        logging::info("another kintsugi-agent invocation is already running; leaving the queue for the next trigger");
        return Ok(());
    };

    let serial_number = system_info::serial_number().context("could not determine a unique identity for this host")?;

    // Reads the identity the check-in already enrolled — this entry point never enrolls one
    // itself, since enrollment belongs with the registration it is part of.
    let Some(identity) = identity::load(&config::identity_dir()) else {
        logging::warn("no enrolled agent identity yet; leaving queued requests for a later trigger");
        return Ok(());
    };

    let client = identity::build_client(Duration::from_secs(30), Some(&identity)).context("failed to build HTTP client")?;
    let mut handler = ServiceHandler::new(&client, &config, &serial_number, &identity);

    queue::process_queue(&config::queue_dir(), &mut handler);

    Ok(())
}

/// The root service's implementation of the queue protocol: the half that holds this host's
/// identity and the privileges to act.
///
/// Used from both root entry points — answering a per-user process's requests in
/// `run_queue_service`, and driving the cycle directly in
/// `patch_unattended_if_nobody_is_logged_in`. That reuse is the point of `RequestHandler` being a
/// trait: `patch_cycle` runs the identical sequence either way, and only the thing on the far side
/// of each call differs.
struct ServiceHandler<'a> {
    client: &'a reqwest::blocking::Client,
    config: &'a Config,
    serial_number: &'a str,
    identity: &'a AgentIdentity,
}

impl<'a> ServiceHandler<'a> {
    fn new(client: &'a reqwest::blocking::Client, config: &'a Config, serial_number: &'a str, identity: &'a AgentIdentity) -> Self {
        Self { client, config, serial_number, identity }
    }
}

impl RequestHandler for ServiceHandler<'_> {
    fn plan(&mut self) -> Result<Plan> {
        let statuses = upgrade::fetch_upgrade_statuses(self.client, self.config, self.serial_number)?;
        let apps = statuses
            .into_iter()
            .filter(|status| upgrade::is_patchable(status, self.identity))
            .map(|status| PlannedApp {
                application_name: status.application_name,
                latest_version: status.latest_version,
            })
            .collect();

        let os_update_available = os_update::check_available().unwrap_or_else(|err| {
            logging::warn(&format!("could not check for OS updates: {err:#}"));
            false
        });

        Ok(Plan { apps, os_update_available })
    }

    /// Re-fetches this host's upgrade paths and verifies the signature before running anything —
    /// the request that got here named an application and nothing more, so this is where that name
    /// becomes something runnable, against the server's answer rather than the requester's.
    fn patch_application(&mut self, application_name: &str) -> Result<()> {
        let statuses = upgrade::fetch_upgrade_statuses(self.client, self.config, self.serial_number)?;
        let status = upgrade::find_patchable(&statuses, application_name, self.identity)
            .with_context(|| format!("the server has no signed, patchable upgrade path for {application_name}"))?;

        upgrade::patch_one(status, self.identity)?;

        match &status.latest_version {
            Some(new_version) => upgrade::report_patch_result(self.client, self.config, self.serial_number, &status.application_name, new_version),
            None => logging::warn(&format!(
                "patched {application_name} successfully, but no latest_version was known to report to the server"
            )),
        }

        Ok(())
    }

    fn install_os_updates(&mut self) -> Result<()> {
        os_update::install()?;
        os_update::report_patched(self.client, self.config, self.serial_number);
        Ok(())
    }
}

/// The per-user process's implementation of the same protocol: every call is a round-trip through
/// the queue to the root service. It holds no identity, makes no network call, and never sees a
/// script or a signature.
struct QueueClient {
    queue_dir: std::path::PathBuf,
}

impl RequestHandler for QueueClient {
    fn plan(&mut self) -> Result<Plan> {
        let result = queue::submit(&self.queue_dir, RequestKind::Plan, "", PLAN_TIMEOUT)?;
        if !result.success {
            anyhow::bail!("the kintsugi-agent service could not work out what is pending: {}", result.output.trim());
        }
        result.data.context("the service answered a plan request without a plan")
    }

    fn patch_application(&mut self, application_name: &str) -> Result<()> {
        let result = queue::submit(&self.queue_dir, RequestKind::AppPatch, application_name, APP_PATCH_TIMEOUT)?;
        if !result.success {
            anyhow::bail!("{}", result.output.trim());
        }
        Ok(())
    }

    fn install_os_updates(&mut self) -> Result<()> {
        let result = queue::submit(&self.queue_dir, RequestKind::OsUpdate, "", OS_UPDATE_TIMEOUT)?;
        if !result.success {
            anyhow::bail!("{}", result.output.trim());
        }
        Ok(())
    }
}

/// The per-user process's job (`--agent`, run by `kintsugi-agent-ui.service` in the logged-in
/// user's own systemd manager): tracks the fleet-wide patching policy and drives the
/// confirm/delay/patch flow once it's due, plus the notification-area icon (progress / next due /
/// Patch Now).
///
/// Like the Windows tray process and unlike the macOS one, it holds no mutual-TLS identity and
/// makes **no network call at all**: it reads the policy out of the cache the root service writes,
/// and asks that service for the work list and for each patch over the queue. See `queue` for why.
///
/// Splits into two threads for the same reason the macOS agent does, though not under the same
/// duress: there, any UI *must* live on the main thread and be driven by a Cocoa event loop. Here
/// nothing imposes that — but the scheduler still needs to block on queue round-trips, 5-minute
/// warnings and modal dialogs, so keeping it off the thread that owns the icon keeps the icon
/// responsive. The two talk one direction each: the scheduler pushes `AgentStatus` updates to the
/// menu (`report`), and a "Patch Now" click sends a signal back over `patch_now_rx`.
fn run_ui_agent() -> Result<()> {
    let config = Config::load();
    let state_dir = config::user_state_dir()?;
    logging::init(&state_dir.join("agent.log"));
    logging::info(&format!(
        "kintsugi-agent (--agent) starting; api_base_url={} state_dir={}",
        config.api_base_url,
        state_dir.display()
    ));

    // Everything this process exists to do — the confirm/delay dialog, the progress window, the
    // notification-area icon — needs a display to do it on. Without one there is nothing here
    // that the root service isn't already doing better, and staying alive would be actively
    // harmful: this process's heartbeat is what tells the root service to leave the schedule
    // alone (see `patch_unattended_if_nobody_is_logged_in`), so an SSH login to a server would
    // otherwise stop that server patching itself for as long as the session lasted.
    //
    // The unit that starts this is ordered after `graphical-session.target` precisely so this
    // check normally passes; it's here for the case where a session manager reaches that target
    // without importing its environment into systemd, and for anyone running `--agent` by hand.
    if !has_a_display() {
        logging::info("no graphical session (neither DISPLAY nor WAYLAND_DISPLAY is set) — nothing for the per-user agent to do here");
        return Ok(());
    }

    let queue_dir = config::queue_dir();
    if !queue_dir.is_dir() {
        // Not necessarily missing — far more often unreachable. `is_dir` cannot distinguish the
        // two from here: the queue is a drop-box inside a state directory this process is not
        // allowed to *list*, so a parent without its execute bit fails the traversal and reports
        // exactly as an absent directory would. 0.5.0 shipped with that parent at `0700` and this
        // warning claiming the service wasn't installed, on hosts where it was installed and
        // checking in fine. See `config::repair_directory_modes`, which is what now fixes it.
        logging::warn(&format!(
            "the request queue directory {} is not reachable (missing, or a parent directory that cannot be traversed) — nothing can be patched from here until the root service's next check-in repairs it",
            queue_dir.display()
        ));
    }

    let policy_cache_path = config::policy_cache_path();
    let schedule_state_path = state_dir.join("schedule.json");

    // Block (retrying) until a policy is available at all — nothing meaningful can be scheduled
    // without one. This process cannot fetch one itself: it holds no mutual-TLS identity (see
    // `config::identity_dir`) and `/api/patching-policy` is inside nginx's client-certificate
    // regex, so the only thing an attempt from here can produce is a 403. The root service fetches
    // it on every check-in and this side reads what landed — the Windows agent's arrangement
    // exactly, and for the identical reason. So the wait covers first-ever startup *and* the
    // ordinary case of logging in before the root service's first check-in has finished.
    let current_policy = loop {
        if let Some(policy) = policy::load_cached(&policy_cache_path) {
            break policy;
        }
        logging::info("waiting for the kintsugi-agent service to publish the patching policy");
        // Recorded even while waiting: a host whose per-user agent is up but hasn't yet seen its
        // first policy is emphatically not a host the root service should start patching behind
        // its back.
        queue::record_heartbeat(&queue_dir);
        std::thread::sleep(AGENT_POLL_INTERVAL);
    };

    let state = ScheduleState::load_or_default(&schedule_state_path, &current_policy);

    let (patch_now_tx, patch_now_rx) = mpsc::channel();
    let report: Box<StatusReporter> = Box::new(tray_menu::report_status);

    // A third thread, for the display half of remote control: the consent dialog, the screen capture
    // and the input injection, all of which need this session's display and none of which the root
    // side can do. It talks to the root agent over a unix socket rather than to the server, because
    // this process still holds no identity — see `remote_ipc`.
    //
    // It returns immediately on a session it cannot serve (Wayland, or no display), which is what
    // leaves the host reporting as unreachable rather than offering a session that would show a
    // black screen.
    std::thread::spawn(remote_session::run);

    std::thread::spawn(move || run_scheduler(current_policy, state, queue_dir, policy_cache_path, patch_now_rx, report));

    // Blocks for the rest of the process's life — this call never returns normally.
    tray_menu::run(patch_now_tx)
}

/// The background half of `run_ui_agent` — see its doc comment for why this is a separate
/// thread. Reports its state to the notification area via `report` at every meaningful
/// transition, and treats a "Patch Now" click the same as a naturally due cycle except it skips
/// the confirm/delay step entirely (see `patch_cycle::run_now`).
fn run_scheduler(
    mut current_policy: policy::PatchingPolicy,
    mut state: ScheduleState,
    queue_dir: std::path::PathBuf,
    policy_cache_path: std::path::PathBuf,
    patch_now_rx: mpsc::Receiver<()>,
    report: Box<StatusReporter>,
) {
    report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });

    let mut handler = QueueClient { queue_dir: queue_dir.clone() };

    loop {
        // Tells the root service this host has somebody driving it, so it doesn't start patching
        // unattended underneath a user who is sitting right there — see
        // `patch_unattended_if_nobody_is_logged_in`. First thing each tick, so a slow cycle below
        // can't let it lapse.
        queue::record_heartbeat(&queue_dir);

        // Re-read rather than re-fetch: the root service refreshes this file on its own schedule
        // (see `run_daemon`), so picking up a policy change here is a local file read, not a
        // network call this process could not make anyway.
        if let Some(refreshed) = policy::load_cached(&policy_cache_path) {
            current_policy = refreshed;
        }

        // Waits on the channel rather than sleeping and polling it once per iteration — a
        // "Patch Now" click wakes this immediately instead of sitting unnoticed for up to
        // AGENT_POLL_INTERVAL, which from the menu just looks like the button did nothing.
        match patch_now_rx.recv_timeout(AGENT_POLL_INTERVAL) {
            Ok(()) => {
                logging::info("scheduler received the Patch Now signal");
                patch_cycle::run_now(&mut handler, &current_policy, &mut state, report.as_ref());
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {
                if state.is_due() {
                    patch_cycle::run(&mut handler, &current_policy, &mut state, report.as_ref());
                }
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                // The sender lives in tray_menu::run for the whole life of the process, so this
                // should never happen — but if it does, fall back to plain polling rather than
                // spin-looping on an instantly-erroring recv.
                logging::error("patch-now channel disconnected unexpectedly");
                std::thread::sleep(AGENT_POLL_INTERVAL);
            }
        }
    }
}

/// Combines the two package managers a Linux desktop actually installs applications with. Any
/// exact (name, version, ...) duplicates are deduplicated, since the backend rejects duplicate
/// (host, name, version) rows in a single report.
///
/// **Distribution packages (dpkg/rpm) are deliberately not reported here**, and this is the one
/// place the Linux inventory differs in kind from the macOS one rather than merely in tooling.
/// They aren't unreported, they're reported *as the OS*: `os_update` patches them wholesale, the
/// same way `softwareupdate` covers everything Apple ships. Listing them individually would mean
/// two thousand rows per host that no upgrade path can ever resolve — `PackageManagerCatalog`
/// recognizes a manager only if its catalog can be queried over HTTP from the API server (which
/// is how `--update-version` works at all), and "the latest version of curl in *this host's*
/// repositories" is not a question Flathub-style global catalogs can answer. Flatpak and Snap
/// can, which is exactly why they're the two that are here.
fn collect_installed_applications() -> Vec<InstalledApp> {
    let mut seen = HashSet::new();
    system_info::scan_flatpak()
        .into_iter()
        .chain(system_info::scan_snap())
        .filter(|app| seen.insert(app.clone()))
        .collect()
}

/// Whether this process is attached to a graphical session — see `run_ui_agent`, its only caller.
/// Both are checked because a Wayland session sets `WAYLAND_DISPLAY` and may or may not also run
/// an X server under `DISPLAY`, while an X11 session sets only the latter.
fn has_a_display() -> bool {
    ["DISPLAY", "WAYLAND_DISPLAY"]
        .iter()
        .any(|name| std::env::var(name).is_ok_and(|value| !value.trim().is_empty()))
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn collect_installed_applications_never_reports_the_same_entry_twice() {
        // On a host with neither manager installed both scans return nothing, which is itself the
        // case worth pinning: an empty inventory is a valid report, not a failure.
        let apps = collect_installed_applications();
        let unique: HashSet<_> = apps.iter().cloned().collect();

        assert_eq!(apps.len(), unique.len());
    }
}
