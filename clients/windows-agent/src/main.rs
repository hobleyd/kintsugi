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
mod remote_desktop;
mod remote_ipc;
mod remote_protocol;
mod remote_session;
mod schedule;
mod screen_capture;
mod self_removal;
mod session_banner;
mod session_launcher;
mod self_update;
mod service;
mod status;
mod system_info;
mod tray_menu;
mod upgrade;
mod win32;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc};
use std::time::Duration;

use anyhow::{Context, Result};

use schedule::ScheduleState;
use status::{AgentStatus, CheckInStatus, MenuAction, StatusReporter, StatusReporterFn};

/// How often the `--agent` loop wakes to check whether a patch cycle is due. Deliberately not tied
/// to the patching interval itself — this is just the scheduler's own tick rate, small enough that
/// a due time (or a delay elapsing, including one that elapsed while the PC was asleep — see
/// `ScheduleState::is_due`) is noticed promptly rather than up to a day late.
const AGENT_POLL_INTERVAL: Duration = Duration::from_secs(60);

/// The Service Control Manager restarts this service on its own if it fails; this bounded retry only
/// exists to ride out the short window at boot where the network isn't up yet.
pub const MAX_ATTEMPTS: u32 = 5;
pub const INITIAL_BACKOFF: Duration = Duration::from_secs(5);

fn main() -> Result<()> {
    // reqwest's rustls backend needs a process-wide default crypto provider installed before any
    // TLS connection is made; with exactly one provider feature compiled in (ring — see Cargo.toml)
    // higher-level callers usually do this themselves, but installing it explicitly, once, up front
    // removes any doubt — install_default() is a harmless no-op error (ignored here) if something
    // else already installed one first.
    let _ = rustls::crypto::ring::default_provider().install_default();

    // A panic in the scheduler thread would otherwise only ever reach the default panic hook's raw
    // stderr — which, for a service and for a windowless tray process alike, goes precisely nowhere.
    // Routing it through the same logger means a silent-looking failure (the scheduler thread dying,
    // with the menu just never updating again) always leaves a trace in the one file this agent's
    // own docs point people at first.
    std::panic::set_hook(Box::new(|info| logging::error(&format!("panic: {info}"))));

    let args: Vec<String> = std::env::args().collect();

    // The session helper, launched by the service as SYSTEM into the logged-in session — never run
    // by hand. Checked before --agent because it is the most privileged mode and the most specific.
    if args.iter().any(|arg| arg == "--remote-session-helper") {
        return run_remote_session_helper();
    }

    // The restart helper a self-update spawns from the newly installed binary — see
    // `self_update::restart_service` for why the service cannot restart itself inline.
    if args.iter().any(|arg| arg == self_update::RESTART_HELPER_ARGUMENT) {
        return run_service_restart_helper();
    }

    if args.iter().any(|arg| arg == "--agent") {
        return run_ui_agent();
    }

    if args.iter().any(|arg| arg == "--check-in") {
        return run_single_check_in();
    }

    // No arguments: started by the Service Control Manager.
    run_service()
}

/// Runs one check-in and exits — what `packaging/install.ps1` calls so a fresh install appears in
/// the fleet immediately, and the one mode that produces readable output when run by hand to
/// diagnose an enrollment problem.
fn run_single_check_in() -> Result<()> {
    logging::init(&config::service_log_path());

    service::Agent::new()?.check_in()
}

/// Stops and starts the service, from outside it, on behalf of the process that just replaced its
/// own binary. Logs to the service's log, since the story it tells is the service's.
fn run_service_restart_helper() -> Result<()> {
    logging::init(&config::service_log_path());
    logging::info(&format!("kintsugi-agent ({}) starting", self_update::RESTART_HELPER_ARGUMENT));

    let result = self_update::run_restart_helper();
    if let Err(err) = &result {
        logging::error(&format!("the service restart helper failed: {err:#}"));
    }
    result
}

// ---------------------------------------------------------------------------------------------
// The service half — the counterpart to the macOS agent's root LaunchDaemon.
// ---------------------------------------------------------------------------------------------

windows_service::define_windows_service!(ffi_service_main, service_main);

fn run_service() -> Result<()> {
    // A failure here is almost always "this wasn't actually started by the SCM" — i.e. someone ran
    // the binary directly with no arguments — so the message names the modes that do work
    // interactively rather than reporting a bare OS error.
    windows_service::service_dispatcher::start(config::SERVICE_NAME, ffi_service_main).context(
        "could not connect to the Service Control Manager. This binary runs as a Windows service \
         with no arguments; to run it interactively use --check-in (one check-in) or --agent (the \
         per-user tray process)",
    )
}

fn service_main(_arguments: Vec<std::ffi::OsString>) {
    if let Err(err) = run_service_inner() {
        logging::error(&format!("the kintsugi-agent service stopped with an error: {err:#}"));
    }
}

fn run_service_inner() -> Result<()> {
    use std::sync::OnceLock;
    use windows_service::service::{ServiceControl, ServiceControlAccept, ServiceExitCode, ServiceState, ServiceStatus, ServiceType};
    use windows_service::service_control_handler::{self, ServiceControlHandlerResult, ServiceStatusHandle};

    let shutdown = Arc::new(AtomicBool::new(false));

    // The handler needs the status handle `register` is about to return, so it reads it through a
    // slot filled in immediately afterwards. A stop cannot arrive before that: the SCM refuses to
    // send controls the service has not yet declared it accepts, and that declaration is the
    // `Running` report below.
    let status_slot: Arc<OnceLock<ServiceStatusHandle>> = Arc::new(OnceLock::new());

    let handler_shutdown = Arc::clone(&shutdown);
    let handler_status = Arc::clone(&status_slot);
    let status_handle = service_control_handler::register(config::SERVICE_NAME, move |control| match control {
        // Interrogate must be answered for the SCM to consider the service responsive at all.
        ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
        // Shutdown (the machine is going down) is accepted alongside Stop so an in-flight check-in
        // is wound up rather than killed — during a patch cycle that matters, since the queue would
        // otherwise be left holding a half-answered request.
        //
        // Reporting StopPending here, rather than leaving the state at Running until `run_loop`
        // returns, is what makes that winding-up survive contact with the tools that stop this
        // service. `Stop-Service` — and so `Restart-Service`, which `install.ps1` and the older
        // self-update helper both relied on — waits two seconds for Stopped and then gives up unless
        // the service is reporting StopPending (PowerShell's `Service.cs`, `DoWaitForStatus`); the
        // loop below polls this flag every two seconds, and may be minutes into a check-in. Without
        // this the stop half of a restart timed out and the start half was never attempted, leaving
        // a freshly updated host running the old binary out of `kintsugi-agent.exe.old` for days.
        ServiceControl::Stop | ServiceControl::Shutdown => {
            handler_shutdown.store(true, Ordering::SeqCst);
            if let Some(handle) = handler_status.get() {
                let _ = handle.set_service_status(ServiceStatus {
                    service_type: ServiceType::OWN_PROCESS,
                    current_state: ServiceState::StopPending,
                    controls_accepted: ServiceControlAccept::empty(),
                    exit_code: ServiceExitCode::Win32(0),
                    checkpoint: 0,
                    wait_hint: service::STOP_WAIT_HINT,
                    process_id: None,
                });
            }
            ServiceControlHandlerResult::NoError
        }
        _ => ServiceControlHandlerResult::NotImplemented,
    })
    .context("could not register the service control handler")?;
    let _ = status_slot.set(status_handle);

    let running_status = ServiceStatus {
        service_type: ServiceType::OWN_PROCESS,
        current_state: ServiceState::Running,
        controls_accepted: ServiceControlAccept::STOP | ServiceControlAccept::SHUTDOWN,
        exit_code: ServiceExitCode::Win32(0),
        checkpoint: 0,
        wait_hint: Duration::default(),
        process_id: None,
    };
    status_handle.set_service_status(running_status.clone())?;

    service::run_loop(shutdown);

    status_handle.set_service_status(ServiceStatus {
        current_state: ServiceState::Stopped,
        controls_accepted: ServiceControlAccept::empty(),
        ..running_status
    })?;

    Ok(())
}

// ---------------------------------------------------------------------------------------------
// The remote control session helper (`--remote-session-helper`).
// ---------------------------------------------------------------------------------------------

/// One remote control session, then exit.
///
/// Launched by the service as SYSTEM inside the logged-in session — see `session_launcher` for why
/// it cannot be the user's own token and `remote_desktop` for what the privilege buys. Not a mode
/// anyone should run by hand: without the service on the other end of the pipe it connects to
/// nothing and exits.
///
/// Logs to the service's own log rather than a per-user one. It runs as SYSTEM, so it has no user
/// state directory to write to, and a session's story is more useful interleaved with the service's
/// than in a file of its own.
fn run_remote_session_helper() -> Result<()> {
    hide_console_window();
    logging::init(&config::service_log_path());
    logging::info("kintsugi-agent (--remote-session-helper) starting");

    let result = remote_session::run();

    match &result {
        Ok(()) => logging::info("the remote control session helper is exiting"),
        Err(err) => logging::error(&format!("the remote control session helper failed: {err:#}")),
    }

    result
}

// ---------------------------------------------------------------------------------------------
// The per-user half (`--agent`) — the counterpart to the macOS agent's LaunchAgent.
// ---------------------------------------------------------------------------------------------

/// Runs continuously in the logged-in user's own session — not elevated, so it can show dialogs and
/// notifications directly, no privilege trickery needed — tracking the fleet-wide patching policy
/// and driving the confirm/delay/patch flow once it's due, plus the notification-area icon
/// (progress / next check-in / next due / Check In Now / Patch Now).
///
/// Splits into two threads for the same reason the macOS agent does: the UI has to keep pumping
/// messages to stay responsive, while the scheduler blocks on queue round trips, five-minute
/// warnings, and modal dialogs. So the *scheduler* runs on a background thread and the *UI* keeps
/// the main one. They talk to each other one direction each: the scheduler pushes `AgentStatus` and
/// `CheckInStatus` updates to the menu (`report`, ultimately a posted window message), and a click on
/// "Check In Now" or "Patch Now" sends a `MenuAction` back to the scheduler over `menu_rx` — see
/// `tray_menu` and `status`.
///
/// Unlike its macOS counterpart, this process holds no mutual-TLS identity and makes no network call
/// at all: it asks the service for the work list and for each patch, over the queue. See `queue` for
/// why.
fn run_ui_agent() -> Result<()> {
    hide_console_window();

    let state_dir = config::user_state_dir()?;
    logging::init(&state_dir.join("agent.log"));
    logging::info(&format!("kintsugi-agent (--agent) starting; state_dir={}", state_dir.display()));

    let policy_cache_path = config::policy_cache_path();
    let schedule_state_path = state_dir.join("schedule.json");

    // Block (retrying) until a policy is available at all — nothing meaningful can be scheduled
    // without one. On macOS this only happens at first-ever startup with no cache and no network;
    // here it also covers the ordinary case of this process starting at logon before the service has
    // completed its first check-in, which is what writes the cache.
    let current_policy = loop {
        if let Some(policy) = policy::load_cached(&policy_cache_path) {
            break policy;
        }
        logging::info("waiting for the agent service to publish the patching policy");
        std::thread::sleep(AGENT_POLL_INTERVAL);
    };

    let state = ScheduleState::load_or_default(&schedule_state_path, &current_policy);

    let (menu_tx, menu_rx) = mpsc::channel();
    let report: StatusReporterFn = tray_menu::report_status;

    std::thread::spawn(move || run_scheduler(current_policy, state, schedule_state_path, policy_cache_path, menu_rx, report));

    // Blocks for the rest of the process's life — this call never returns normally.
    tray_menu::run(menu_tx)
}

/// Hides the console window this process was given, so the tray agent doesn't flash a black box in
/// the user's face every time the logon task starts it.
///
/// The alternative — linking the whole binary as a GUI subsystem application
/// (`#![windows_subsystem = "windows"]`) — would take the console away from the *other* two modes
/// too, and `--check-in` exists specifically so an administrator can run this by hand and read what
/// happens. Hiding the window keeps that working while still solving the flash. The console itself
/// is deliberately left attached rather than freed: `logging` still writes to it, and detaching
/// would make those writes fail.
fn hide_console_window() {
    use windows_sys::Win32::System::Console::GetConsoleWindow;
    use windows_sys::Win32::UI::WindowsAndMessaging::{ShowWindow, SW_HIDE};

    // SAFETY: GetConsoleWindow returns null when this process has no console (e.g. it was started
    // from a GUI shell), which is checked before use; ShowWindow on a live window handle is safe.
    unsafe {
        let console = GetConsoleWindow();
        if !console.is_null() {
            ShowWindow(console, SW_HIDE);
        }
    }
}


/// How a patch cycle is started — `patch_cycle::run` for a naturally due one, `run_now` for a
/// "Patch Now" click. Their signatures are identical, so `spawn_cycle` is handed whichever one is
/// meant rather than a flag to branch on.
type CycleFn = fn(&policy::PatchingPolicy, &mut ScheduleState, &StatusReporter);

/// Runs one patch cycle on its own thread, handing it the schedule state for the duration and
/// getting it back when it finishes.
///
/// A thread rather than a plain call, because the confirmation dialog stands there for a whole
/// delay period and the scheduler used to be parked inside it for all of it: the "Next check-in"
/// line stopped updating, and a menu click sat in the channel until the dialog came down, which
/// from the menu looks like the item did nothing. (The Linux agent had a third and worse symptom —
/// see its own copy of this comment.)
///
/// The state goes *with* the cycle rather than being shared behind a lock: a mutex held for the
/// hours a dialog can stand there would have moved the block rather than removed it, since
/// `is_due` would then be the thing waiting. So `state` being `None` in `run_scheduler`'s loop is
/// exactly "a cycle is in flight", and it is what stops a second one starting.
///
/// Everything else is cheap to hand over — the policy is a small struct, `report` is a function
/// pointer — and each cycle works from the policy as it was when it started, which is what the
/// previous arrangement did too: the loop's re-read only ever affected the *next* cycle.
fn spawn_cycle(
    cycle: CycleFn,
    policy: &policy::PatchingPolicy,
    mut state: ScheduleState,
    report: StatusReporterFn,
) -> std::thread::JoinHandle<ScheduleState> {
    let policy = policy.clone();
    std::thread::spawn(move || {
        cycle(&policy, &mut state, &report);
        state
    })
}

/// The background half of `run_ui_agent` — see its doc comment for why this is a separate thread.
/// Reports its state to the menu via `report` at every meaningful transition, and treats a "Patch
/// Now" click the same as a naturally due cycle except it skips the confirm/delay step entirely (see
/// `patch_cycle::run_now`). A "Check In Now" click goes to the service through the queue (see
/// `checkin_schedule::request_now`).
fn run_scheduler(
    mut current_policy: policy::PatchingPolicy,
    state: ScheduleState,
    schedule_state_path: std::path::PathBuf,
    policy_cache_path: std::path::PathBuf,
    menu_rx: mpsc::Receiver<MenuAction>,
    report: StatusReporterFn,
) {
    report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });

    // `None` for exactly as long as a cycle owns the schedule state — see `spawn_cycle`, which is
    // also why this loop can be sure it is never running two.
    let mut state = Some(state);
    let mut in_flight: Option<std::thread::JoinHandle<ScheduleState>> = None;

    // The service's schedule, as last shown in the menu. Re-read every tick — the service persists
    // a minute on its first run and the server may move it on any check-in — but only pushed to the
    // menu when the answer changes, which is once an hour: `tray_menu::format_due` shells out to
    // PowerShell, and there is no reason to do that once a minute for a line that has not moved.
    let checkin_schedule_path = config::checkin_schedule_path();
    let mut shown_check_in: Option<CheckInStatus> = None;

    loop {
        // Takes the schedule state back from a cycle that has finished, and says so. Reporting
        // here rather than trusting the cycle to is what covers its early returns — an
        // unreachable service, nothing to patch, a dialog that would not launch — since those
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
            if let Some(current) = state.as_ref() {
                report(AgentStatus::Idle { next_due_epoch: current.next_due_epoch() });
            }
        }

        // Re-read rather than re-fetch: the service refreshes this file on its own schedule (see
        // `service::check_in`), so picking up a policy change here is a local file read, not a
        // network call this process couldn't make anyway.
        if let Some(refreshed) = policy::load_cached(&policy_cache_path) {
            current_policy = refreshed;
        }

        let next_check_in = CheckInStatus::Scheduled { next_epoch: checkin_schedule::next_check_in_epoch(&checkin_schedule_path) };
        if shown_check_in != Some(next_check_in) {
            tray_menu::report_check_in(next_check_in);
            shown_check_in = Some(next_check_in);
        }

        // Waits on the channel rather than sleeping and polling it once per iteration — a click in
        // the menu wakes this immediately instead of sitting unnoticed for up to AGENT_POLL_INTERVAL,
        // which from the menu just looks like the item did nothing.
        match menu_rx.recv_timeout(AGENT_POLL_INTERVAL) {
            Ok(MenuAction::PatchNow) => {
                logging::info("scheduler received the Patch Now signal");
                match state.take() {
                    Some(owned) => in_flight = Some(spawn_cycle(patch_cycle::run_now, &current_policy, owned, report)),
                    // The menu greys both actions whenever this can happen, so getting here means a
                    // click was already on its way — which is worth saying, since this is an
                    // explicit action the user just took and silence would read as it being lost.
                    None => {
                        logging::info("Patch Now ignored: a patch cycle is already running");
                        dialogs::notify("Kintsugi Patching", "A patch cycle is already running.");
                    }
                }
            }
            Ok(MenuAction::CheckInNow) => {
                logging::info("scheduler received the Check In Now signal");
                tray_menu::report_check_in(CheckInStatus::InProgress);
                checkin_schedule::request_now(&config::queue_dir());
                // Forces the schedule line back at the top of the next tick, whatever it now says —
                // the server may just have moved this host's minute.
                shown_check_in = None;
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {
                if state.as_ref().is_some_and(|current| current.is_due()) {
                    let owned = state.take().expect("just checked that the state is here");
                    in_flight = Some(spawn_cycle(patch_cycle::run, &current_policy, owned, report));
                }
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                // The sender lives in tray_menu for the whole life of the process, so this should
                // never happen — but if it does, fall back to plain polling rather than spin-looping
                // on an instantly-erroring recv.
                logging::error("menu action channel disconnected unexpectedly");
                std::thread::sleep(AGENT_POLL_INTERVAL);
            }
        }
    }
}
