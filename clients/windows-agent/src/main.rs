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
use status::{AgentStatus, CheckInStatus, MenuAction, StatusReporter};

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
    use windows_service::service::{ServiceControl, ServiceControlAccept, ServiceExitCode, ServiceState, ServiceStatus, ServiceType};
    use windows_service::service_control_handler::{self, ServiceControlHandlerResult};

    let shutdown = Arc::new(AtomicBool::new(false));

    let handler_shutdown = Arc::clone(&shutdown);
    let status_handle = service_control_handler::register(config::SERVICE_NAME, move |control| match control {
        // Interrogate must be answered for the SCM to consider the service responsive at all.
        ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
        // Shutdown (the machine is going down) is accepted alongside Stop so an in-flight check-in
        // is wound up rather than killed — during a patch cycle that matters, since the queue would
        // otherwise be left holding a half-answered request.
        ServiceControl::Stop | ServiceControl::Shutdown => {
            handler_shutdown.store(true, Ordering::SeqCst);
            ServiceControlHandlerResult::NoError
        }
        _ => ServiceControlHandlerResult::NotImplemented,
    })
    .context("could not register the service control handler")?;

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
    let report: Box<StatusReporter> = Box::new(tray_menu::report_status);

    std::thread::spawn(move || run_scheduler(current_policy, state, policy_cache_path, menu_rx, report));

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

/// The background half of `run_ui_agent` — see its doc comment for why this is a separate thread.
/// Reports its state to the menu via `report` at every meaningful transition, and treats a "Patch
/// Now" click the same as a naturally due cycle except it skips the confirm/delay step entirely (see
/// `patch_cycle::run_now`). A "Check In Now" click goes to the service through the queue (see
/// `checkin_schedule::request_now`).
fn run_scheduler(
    mut current_policy: policy::PatchingPolicy,
    mut state: ScheduleState,
    policy_cache_path: std::path::PathBuf,
    menu_rx: mpsc::Receiver<MenuAction>,
    report: Box<StatusReporter>,
) {
    report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });

    // The service's schedule, as last shown in the menu. Re-read every tick — the service persists
    // a minute on its first run and the server may move it on any check-in — but only pushed to the
    // menu when the answer changes, which is once an hour: `tray_menu::format_due` shells out to
    // PowerShell, and there is no reason to do that once a minute for a line that has not moved.
    let checkin_schedule_path = config::checkin_schedule_path();
    let mut shown_check_in: Option<CheckInStatus> = None;

    loop {
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
                patch_cycle::run_now(&current_policy, &mut state, report.as_ref());
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
                if state.is_due() {
                    patch_cycle::run(&current_policy, &mut state, report.as_ref());
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
