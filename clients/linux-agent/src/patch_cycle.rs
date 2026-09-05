use std::time::Duration;

use crate::dialogs::{self, ConfirmChoice};
use crate::logging;
use crate::policy::PatchingPolicy;
use crate::queue::{Plan, RequestHandler};
use crate::schedule::ScheduleState;
use crate::status::{AgentStatus, StatusReporter};

const WARNING_PERIOD: Duration = Duration::from_secs(5 * 60);

/// How a cycle presents itself.
///
/// The macOS agent has only the interactive shape, because its scheduler only ever runs inside a
/// logged-in user's session. Linux needs both: a graphical desktop gets the same confirm / warn /
/// progress flow every other platform gets, and a server — where there is no session, no display,
/// and nobody to ask — gets the same patching with none of it. See `run_unattended`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Presentation {
    Interactive { show_warning: bool },
    Unattended,
}

impl Presentation {
    fn is_interactive(self) -> bool {
        matches!(self, Presentation::Interactive { .. })
    }

    fn notify(self, message: &str) {
        if self.is_interactive() {
            dialogs::notify("Kintsugi Patching", message);
        }
    }
}

/// Fetches the current work list. On the per-user side this is a `queue::RequestKind::Plan`
/// round-trip to the root service; on the root side the very same call runs inline. Either way
/// it is kept separate from, and always run *before*, any dialog — a confirm prompt (or the "no
/// delays left" acknowledgement) has no business appearing if the server can't even be reached to
/// say whether there's anything to patch, or if it says there isn't.
fn plan(handler: &mut impl RequestHandler) -> anyhow::Result<Plan> {
    handler.plan()
}

/// Runs one full due patch cycle: check what's actually pending, confirm (or delay) only if
/// there's real work, a 5-minute warning, applications, then the OS if an update is available —
/// in that order, per the policy. `report` is how the notification area (which this module knows
/// nothing about) is kept in sync with what's happening.
///
/// Returns without doing anything destructive if the server can't be reached, if there's nothing
/// to patch, or if the user chooses to delay — in every case, the next poll tick (or the next due
/// time) picks it back up; none of these are treated as failures.
pub fn run(handler: &mut impl RequestHandler, policy: &PatchingPolicy, state: &mut ScheduleState, report: &StatusReporter) {
    let work = match plan(handler) {
        Ok(work) => work,
        Err(err) => {
            // Deliberately no dialog and no state change here: the server being briefly
            // unreachable (a deploy, a network blip) isn't something the user needs to be
            // interrupted about, and it costs nothing to just try again at the next poll tick.
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

    match confirm_or_delay(policy, state, &work.app_names(), work.os_update_available, report) {
        Ok(false) => return, // delayed — nothing more to do until the new due time arrives
        Ok(true) => {}
        Err(err) => {
            // Unlike macOS, where `osascript` is part of the operating system and a failure here
            // really does mean something is wrong, a Linux desktop may simply have no zenity or
            // kdialog installed. Proceeding is the right call: the policy says this host is due,
            // the user has been notified as far as this host is able to notify them, and refusing
            // to patch forever because a dialog program is missing would leave the machine
            // permanently unpatched — the one outcome worse than patching unannounced.
            logging::warn(&format!("could not show the patching confirmation dialog, proceeding without it: {err:#}"));
        }
    }

    execute(handler, policy, state, work, report, Presentation::Interactive { show_warning: true });
}

/// The notification area's "Patch Now" item: skips both the confirm/delay decision (asking
/// whether to delay makes no sense when the user just explicitly asked to patch right now) and
/// the 5-minute warning (that warning exists to give notice before an *automatic* start; clicking
/// this already is that notice) — goes straight into patching, once it's confirmed there's
/// actually something to do.
pub fn run_now(handler: &mut impl RequestHandler, policy: &PatchingPolicy, state: &mut ScheduleState, report: &StatusReporter) {
    logging::info("Patch Now triggered manually from the notification area");

    let work = match plan(handler) {
        Ok(work) => work,
        Err(err) => {
            logging::warn(&format!("could not check for pending patches: {err:#}"));
            dialogs::notify("Kintsugi Patching", "Could not check for updates — is the server reachable?");
            return;
        }
    };

    if work.is_empty() {
        dialogs::notify("Kintsugi Patching", "Nothing to patch right now.");
        state.register_completed(policy);
        report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });
        return;
    }

    execute(handler, policy, state, work, report, Presentation::Interactive { show_warning: false });
}

/// The root service's own cycle, for a host with nobody logged in to run the per-user half.
///
/// This has no counterpart on macOS or Windows, and it exists because of what a Linux fleet
/// actually looks like: on those platforms a managed host is somebody's desktop, and putting the
/// schedule in the per-user process costs nothing. Here most managed hosts are servers with no
/// graphical session at all, so the same design would mean the majority of the fleet silently
/// never patched — the exact class of quiet no-op this system is built to prevent.
///
/// Everything the interactive path does *for the user's benefit* is dropped rather than faked:
/// there is no confirmation (nobody to ask), no delay (a delay is a person asking for more time),
/// and no five-minute warning (nobody to warn). The policy's interval still governs when this
/// runs, and the same signed scripts still do the work.
///
/// Only ever called when no per-user agent has recently claimed this host — see
/// `main::run_daemon`.
pub fn run_unattended(handler: &mut impl RequestHandler, policy: &PatchingPolicy, state: &mut ScheduleState) {
    if !state.is_due() {
        return;
    }

    let work = match plan(handler) {
        Ok(work) => work,
        Err(err) => {
            logging::warn(&format!("could not check for pending patches, will retry at the next check-in: {err:#}"));
            return;
        }
    };

    if work.is_empty() {
        logging::info("nothing to patch at this check");
        state.register_completed(policy);
        return;
    }

    logging::info(&format!(
        "no per-user agent is running on this host; patching unattended ({} application(s), os_update_available={})",
        work.apps.len(),
        work.os_update_available
    ));

    execute(handler, policy, state, work, &|_| {}, Presentation::Unattended);
}

fn execute(
    handler: &mut impl RequestHandler,
    policy: &PatchingPolicy,
    state: &mut ScheduleState,
    work: Plan,
    report: &StatusReporter,
    presentation: Presentation,
) {
    if let Presentation::Interactive { show_warning: true } = presentation {
        let warning_message = format!("Patching will begin in {} minutes. Please save your work.", WARNING_PERIOD.as_secs() / 60);
        dialogs::notify("Kintsugi Patching", &warning_message);
        report(AgentStatus::Patching { current: warning_message, completed: 0, total: 0 });
        std::thread::sleep(WARNING_PERIOD);
    }

    presentation.notify("Patching has started — do not turn off your computer.");
    logging::info("patch cycle starting");

    let (succeeded, failed) = run_patches(handler, work, report, presentation);

    let summary = if failed == 0 {
        format!("Patching complete — {succeeded} item(s) updated.")
    } else {
        format!("Patching finished with issues — {succeeded} succeeded, {failed} failed. Check the logs.")
    };
    presentation.notify(&summary);
    logging::info(&format!("patch cycle finished: {summary}"));

    state.register_completed(policy);
    report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });
}

/// Returns `Ok(true)` to proceed with patching now, `Ok(false)` if the user chose to delay (or
/// the dialog just sat there unanswered — see `dialogs::confirm_patch`'s `TimedOut`).
fn confirm_or_delay(
    policy: &PatchingPolicy,
    state: &mut ScheduleState,
    app_names: &[String],
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
        app_names,
        os_update_available,
        policy.delay_seconds(),
    )?;

    match choice {
        ConfirmChoice::PatchNow => Ok(true),
        // An ignored dialog counts down the delay budget exactly like an explicit delay would:
        // it consumed one delay period's worth of time, so it consumes one delay. The next poll
        // tick re-shows the dialog (via `run`) with the count decremented, until either the user
        // responds or the budget hits zero and the unconditional-proceed branch above takes over.
        ConfirmChoice::Delay | ConfirmChoice::TimedOut => {
            state.register_delay(policy);
            report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });
            Ok(false)
        }
    }
}

/// Applications first, then the OS — per the policy's intent, application updates are the
/// frequent, low-risk case, while an OS update is the more disruptive one (likely to need a
/// restart) best left until everything else is already current. Returns (succeeded, failed)
/// counts across both.
fn run_patches(handler: &mut impl RequestHandler, work: Plan, report: &StatusReporter, presentation: Presentation) -> (usize, usize) {
    let Plan { apps, os_update_available } = work;
    let total = apps.len() + usize::from(os_update_available);
    let mut completed = 0;
    let mut succeeded = 0;
    let mut failed = 0;

    for app in &apps {
        let target = match &app.latest_version {
            Some(version) => format!("{} \u{2192} {version}", app.application_name),
            None => app.application_name.clone(),
        };
        presentation.notify(&format!("Patching {target}\n{}", dialogs::progress_bar(completed, total)));
        report(AgentStatus::Patching { current: target, completed, total });

        logging::info(&format!("attempting to patch {}", app.application_name));
        match handler.patch_application(&app.application_name) {
            Ok(()) => {
                succeeded += 1;
                logging::info(&format!("patched {} successfully", app.application_name));
            }
            Err(err) => {
                failed += 1;
                logging::error(&format!("failed to patch {}: {err:#}", app.application_name));
            }
        }
        completed += 1;
    }

    if os_update_available {
        let current = "Installing system updates — this may take a while".to_string();
        presentation.notify(&format!("{current}\n{}", dialogs::progress_bar(completed, total)));
        report(AgentStatus::Patching { current, completed, total });

        logging::info("attempting to install OS updates");
        match handler.install_os_updates() {
            Ok(()) => {
                succeeded += 1;
                logging::info("OS updates installed successfully");
            }
            Err(err) => {
                failed += 1;
                logging::error(&format!("could not install OS updates: {err:#}"));
            }
        }
    }

    (succeeded, failed)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::queue::PlannedApp;

    /// Stands in for either side of the queue: the tests below care about what a cycle *asks for*
    /// and in what order, which is identical whether the answer comes from a round-trip to the
    /// root service or from running the work inline.
    #[derive(Default)]
    struct RecordingHandler {
        plan: Plan,
        actions: Vec<String>,
        fail_patches: bool,
    }

    impl RequestHandler for RecordingHandler {
        fn plan(&mut self) -> anyhow::Result<Plan> {
            self.actions.push("plan".to_string());
            Ok(self.plan.clone())
        }

        fn patch_application(&mut self, application_name: &str) -> anyhow::Result<()> {
            self.actions.push(format!("patch:{application_name}"));
            if self.fail_patches {
                anyhow::bail!("the upgrade script exited non-zero");
            }
            Ok(())
        }

        fn install_os_updates(&mut self) -> anyhow::Result<()> {
            self.actions.push("os-update".to_string());
            Ok(())
        }

        fn check_in(&mut self) -> anyhow::Result<String> {
            self.actions.push("check-in".to_string());
            Ok("checked in".to_string())
        }
    }

    fn policy() -> PatchingPolicy {
        PatchingPolicy::for_test(1, 1, 3)
    }

    fn scratch_state(name: &str, policy: &PatchingPolicy) -> ScheduleState {
        let path = std::env::temp_dir().join(format!("kintsugi-patch-cycle-{name}-{}.json", std::process::id()));
        let _ = std::fs::remove_file(&path);
        ScheduleState::load_or_default(&path, policy)
    }

    fn plan_with(apps: &[&str], os_update_available: bool) -> Plan {
        Plan {
            apps: apps
                .iter()
                .map(|name| PlannedApp { application_name: name.to_string(), latest_version: Some("2.0".to_string()) })
                .collect(),
            os_update_available,
        }
    }

    #[test]
    fn run_unattended_does_nothing_at_all_until_the_cycle_is_due() {
        let policy = policy();
        // A fresh state is deliberately not due until one full interval has passed — see
        // `ScheduleState::load_or_default`.
        let mut state = scratch_state("not-due", &policy);
        let mut handler = RecordingHandler { plan: plan_with(&["Firefox"], true), ..Default::default() };

        run_unattended(&mut handler, &policy, &mut state);

        assert!(handler.actions.is_empty(), "nothing should even be asked for before the cycle is due");
    }

    #[test]
    fn run_unattended_patches_applications_before_the_os() {
        let policy = policy();
        let mut state = scratch_state("ordering", &policy);
        state.force_due_for_test();
        let mut handler = RecordingHandler { plan: plan_with(&["Firefox", "GIMP"], true), ..Default::default() };

        run_unattended(&mut handler, &policy, &mut state);

        assert_eq!(
            handler.actions,
            vec!["plan", "patch:Firefox", "patch:GIMP", "os-update"],
            "applications come first, the OS last"
        );
    }

    #[test]
    fn run_unattended_reschedules_and_stops_when_there_is_nothing_to_do() {
        let policy = policy();
        let mut state = scratch_state("empty", &policy);
        state.force_due_for_test();
        let mut handler = RecordingHandler { plan: Plan::default(), ..Default::default() };

        run_unattended(&mut handler, &policy, &mut state);

        assert_eq!(handler.actions, vec!["plan"]);
        assert!(!state.is_due(), "an empty cycle still counts as completed, so the next one is a full interval away");
    }

    /// One application failing must not abandon the rest of the cycle — including the OS update,
    /// which is the step most likely to matter.
    #[test]
    fn run_unattended_carries_on_after_a_failed_application() {
        let policy = policy();
        let mut state = scratch_state("failure", &policy);
        state.force_due_for_test();
        let mut handler = RecordingHandler {
            plan: plan_with(&["Firefox", "GIMP"], true),
            fail_patches: true,
            ..Default::default()
        };

        run_unattended(&mut handler, &policy, &mut state);

        assert_eq!(handler.actions, vec!["plan", "patch:Firefox", "patch:GIMP", "os-update"]);
        assert!(!state.is_due(), "a cycle that finished with failures is still a finished cycle");
    }

    #[test]
    fn run_patches_counts_successes_and_failures_separately() {
        let mut handler = RecordingHandler { fail_patches: true, ..Default::default() };

        let (succeeded, failed) = run_patches(&mut handler, plan_with(&["Firefox", "GIMP"], true), &|_| {}, Presentation::Unattended);

        // Both applications fail; the OS update (which this handler always succeeds at) does not.
        assert_eq!((succeeded, failed), (1, 2));
    }
}
