use std::time::Duration;

use crate::config;
use crate::dialogs::{self, ConfirmChoice};
use crate::logging;
use crate::policy::PatchingPolicy;
use crate::queue::{self, Plan, RequestKind};
use crate::schedule::ScheduleState;
use crate::status::{AgentStatus, StatusReporter};

const WARNING_PERIOD: Duration = Duration::from_secs(5 * 60);

/// How long to wait for the service to answer a `Plan` request. Short — it's one HTTP call plus a
/// Windows Update search — but not so short that a slow link makes a due cycle silently vanish.
const PLAN_TIMEOUT: Duration = Duration::from_secs(3 * 60);

/// How long to wait for one application's upgrade. Generous: a large installer over a slow link,
/// downloaded and then run silently, legitimately takes a while.
const APP_PATCH_TIMEOUT: Duration = Duration::from_secs(30 * 60);

/// How long to wait for the Windows Update install. Windows updates can legitimately take a very
/// long time to download and install, so this matches the macOS agent's own OS-update budget.
const OS_UPDATE_TIMEOUT: Duration = Duration::from_secs(60 * 60);

/// Asks the service what's actually pending. Kept separate from, and always run *before*, any
/// dialog — a confirm prompt (or the "no delays left" acknowledgement) has no business appearing if
/// the server can't even be reached to say whether there's anything to patch, or if it says there
/// isn't.
///
/// The macOS agent computes this itself; here it's a queue round trip, because the tray process
/// holds no mutual-TLS identity and so cannot ask the server anything directly. See `queue`.
fn plan() -> anyhow::Result<Plan> {
    let result = queue::submit(&config::queue_dir(), RequestKind::Plan, "", PLAN_TIMEOUT)?;
    if !result.success {
        anyhow::bail!("the agent service could not determine what needs patching: {}", result.output);
    }
    result.data.ok_or_else(|| anyhow::anyhow!("the agent service answered a plan request with no plan"))
}

/// Runs one full due patch cycle: check what's actually pending, confirm (or delay) only if there's
/// real work, a 5-minute warning, applications, then the OS if an update is available — in that
/// order, per the policy. `report` is how the notification-area menu (which this module knows
/// nothing about) is kept in sync with what's happening.
///
/// Returns without doing anything destructive if the service can't be reached, if there's nothing
/// to patch, or if the user chooses to delay — in every case, the next poll tick (or the next due
/// time) picks it back up; none of these are treated as failures.
pub fn run(policy: &PatchingPolicy, state: &mut ScheduleState, report: &StatusReporter) {
    let work = match plan() {
        Ok(work) => work,
        Err(err) => {
            // Deliberately no dialog and no state change here: the server being briefly unreachable
            // (a deploy, a network blip) isn't something the user needs to be interrupted about,
            // and it costs nothing to just try again at the next poll tick.
            logging::warn(&format!("could not check for pending patches, will retry at the next check: {err:#}"));
            return;
        }
    };

    if work.is_empty() {
        logging::info("nothing to patch at this check");
        state.register_completed(policy);
        report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });
        return;
    }

    match confirm_or_delay(policy, state, work.apps.len(), work.os_update_available, report) {
        Ok(false) => return, // delayed — nothing more to do until the new due time arrives
        Ok(true) => {}
        Err(err) => {
            logging::warn(&format!("could not show the patching confirmation dialog, will retry at the next check: {err:#}"));
            return;
        }
    }

    execute(policy, state, work, report, true);
}

/// The menu's "Patch Now" item: skips both the confirm/delay decision (asking whether to delay makes
/// no sense when the user just explicitly asked to patch right now) and the 5-minute warning (that
/// warning exists to give notice before an *automatic* start; clicking this item already is that
/// notice) — goes straight into patching, once it's confirmed there's actually something to do.
pub fn run_now(policy: &PatchingPolicy, state: &mut ScheduleState, report: &StatusReporter) {
    logging::info("Patch Now triggered manually from the notification area");

    let work = match plan() {
        Ok(work) => work,
        Err(err) => {
            logging::warn(&format!("could not check for pending patches: {err:#}"));
            dialogs::notify("Kintsugi Patching", "Could not check for updates — is the agent service running?");
            return;
        }
    };

    if work.is_empty() {
        dialogs::notify("Kintsugi Patching", "Nothing to patch right now.");
        state.register_completed(policy);
        report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });
        return;
    }

    execute(policy, state, work, report, false);
}

fn execute(policy: &PatchingPolicy, state: &mut ScheduleState, work: Plan, report: &StatusReporter, show_warning: bool) {
    if show_warning {
        let warning_message = format!("Patching will begin in {} minutes. Please save your work.", WARNING_PERIOD.as_secs() / 60);
        dialogs::notify("Kintsugi Patching", &warning_message);
        report(AgentStatus::Patching { current: warning_message, completed: 0, total: 0 });
        std::thread::sleep(WARNING_PERIOD);
    }

    dialogs::notify("Kintsugi Patching", "Patching has started — do not turn off your computer.");
    logging::info("patch cycle starting");

    let (succeeded, failed) = run_patches(work, report);

    let summary = if failed == 0 {
        format!("Patching complete — {succeeded} item(s) updated.")
    } else {
        format!("Patching finished with issues — {succeeded} succeeded, {failed} failed. Check the logs.")
    };
    dialogs::notify("Kintsugi Patching", &summary);
    logging::info(&format!("patch cycle finished: {summary}"));

    state.register_completed(policy);
    report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });
}

/// Returns `Ok(true)` to proceed with patching now, `Ok(false)` if the user chose to delay (or the
/// dialog just sat there unanswered — see `dialogs::confirm_patch`'s `TimedOut`).
fn confirm_or_delay(
    policy: &PatchingPolicy,
    state: &mut ScheduleState,
    app_count: usize,
    os_update_available: bool,
    report: &StatusReporter,
) -> anyhow::Result<bool> {
    if !state.can_delay(policy) {
        dialogs::acknowledge(
            "The maximum number of delays has been used — patching will now proceed.",
            WARNING_PERIOD.as_secs(),
        )?;
        return Ok(true);
    }

    let choice = dialogs::confirm_patch(
        &policy.delay_label(),
        state.delays_remaining(policy),
        app_count,
        os_update_available,
        policy.delay_seconds(),
    )?;

    match choice {
        ConfirmChoice::PatchNow => Ok(true),
        // An ignored dialog counts down the delay budget exactly like an explicit delay would: it
        // consumed one delay period's worth of time, so it consumes one delay. The next poll tick
        // re-shows the dialog (via `run`) with the count decremented, until either the user
        // responds or the budget hits zero and the unconditional-proceed branch above takes over.
        ConfirmChoice::Delay | ConfirmChoice::TimedOut => {
            state.register_delay(policy);
            report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });
            Ok(false)
        }
    }
}

/// Applications first, then the OS — per the policy's intent, application updates are the frequent,
/// low-risk case, while an OS update is the more disruptive one (likely to need a restart) best
/// left until everything else is already current. Returns (succeeded, failed) counts across both.
///
/// Every step here is a queue round trip: this process is deliberately not privileged enough to
/// install anything itself, and reporting each result back to the server is the service's job too
/// (it's the side holding the identity). See `queue`.
fn run_patches(work: Plan, report: &StatusReporter) -> (usize, usize) {
    let Plan { apps, os_update_available } = work;
    let total = apps.len() + usize::from(os_update_available);
    let queue_dir = config::queue_dir();
    let mut completed = 0;
    let mut succeeded = 0;
    let mut failed = 0;

    for app in &apps {
        let target = match &app.latest_version {
            Some(version) => format!("{} \u{2192} {version}", app.application_name),
            None => app.application_name.clone(),
        };
        dialogs::notify("Kintsugi Patching", &format!("Patching {target}\n{}", dialogs::progress_bar(completed, total)));
        report(AgentStatus::Patching { current: target, completed, total });

        logging::info(&format!("asking the agent service to patch {}", app.application_name));
        match queue::submit(&queue_dir, RequestKind::AppPatch, &app.application_name, APP_PATCH_TIMEOUT) {
            Ok(result) if result.success => {
                succeeded += 1;
                logging::info(&format!("patched {} successfully", app.application_name));
            }
            Ok(result) => {
                failed += 1;
                logging::error(&format!("failed to patch {}: {}", app.application_name, result.output));
            }
            Err(err) => {
                failed += 1;
                logging::error(&format!("failed to patch {}: {err:#}", app.application_name));
            }
        }
        completed += 1;
    }

    if os_update_available {
        let current = "Installing Windows updates — this may take a while".to_string();
        dialogs::notify("Kintsugi Patching", &format!("{current}\n{}", dialogs::progress_bar(completed, total)));
        report(AgentStatus::Patching { current, completed, total });

        logging::info("asking the agent service to install Windows updates");
        match queue::submit(&queue_dir, RequestKind::OsUpdate, "", OS_UPDATE_TIMEOUT) {
            Ok(result) if result.success => {
                succeeded += 1;
                logging::info("Windows updates installed successfully");
            }
            Ok(result) => {
                failed += 1;
                logging::error(&format!("Windows update install reported failure: {}", result.output));
            }
            Err(err) => {
                failed += 1;
                logging::error(&format!("could not install Windows updates: {err:#}"));
            }
        }
    }

    (succeeded, failed)
}
