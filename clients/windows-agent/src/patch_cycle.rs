use std::time::Duration;

use crate::config;
use crate::dialogs::{self, ConfirmChoice};
use crate::logging;
use crate::policy::PatchingPolicy;
use crate::queue::{self, Plan, RequestKind};
use crate::schedule::{self, ScheduleState};
use crate::status::{AgentStatus, StatusReporter};

/// How much notice an automatic start gives, *in total* — the "no delays left" dialog is part of
/// it rather than something served before it, so a host nobody is sitting at waits five minutes
/// from that dialog appearing to patching starting, not five plus however long the dialog stood
/// there. `confirm_or_delay` measures what the dialog used and hands `execute` the remainder.
const WARNING_PERIOD: Duration = Duration::from_secs(5 * 60);

/// The shortest remainder still worth waiting out — see `remaining_warning`.
const MINIMUM_WARNING: Duration = Duration::from_secs(30);

/// What is left of the notice after the "no delays left" dialog has stood there for `stood_for`.
/// This is the rule that keeps an automatic start's warning five minutes *in total* rather than
/// five on top of however long that dialog was up, so somebody who reads it and clicks OK still
/// gets the rest of the period to save their work and a host with nobody at it waits five minutes
/// rather than ten.
///
/// A remainder under `MINIMUM_WARNING` comes back as none at all: when the dialog runs the period
/// out on its own the leftover is a second or two of rounding, and a notification promising "1
/// minute" that is really four seconds says less than saying nothing. Kept identical in the other
/// two agents.
fn remaining_warning(stood_for: Duration) -> Duration {
    let remaining = WARNING_PERIOD.saturating_sub(stood_for);
    if remaining < MINIMUM_WARNING {
        Duration::ZERO
    } else {
        remaining
    }
}

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
/// real work, a 5-minute warning unless the user asked for patching to start now, applications, then
/// the OS if an update is available — in that order, per the policy. `report` is how the notification-area menu (which this module knows
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

    let warning = match confirm_or_delay(policy, state, &work.app_names(), work.os_update_available, report) {
        Ok(Decision::PatchNow) => Duration::ZERO,
        Ok(Decision::ProceedAfterWarning { remaining }) => remaining,
        // Both delaying answers stop here: `confirm_or_delay` has already moved the due time, and
        // the next tick picks the cycle back up — at once for an unanswered dialog, a delay period
        // later for an explicit "Delay".
        Ok(Decision::Delayed) | Ok(Decision::Unanswered) => return,
        Err(err) => {
            logging::warn(&format!("could not show the patching confirmation dialog, will retry at the next check: {err:#}"));
            return;
        }
    };

    execute(policy, state, work, report, warning);
}

/// The menu's "Patch Now" item: skips the confirm/delay decision altogether, since asking whether to
/// delay makes no sense when the user just explicitly asked to patch right now, and goes straight
/// into patching once it's confirmed there's actually something to do. It also skips the 5-minute
/// warning, for the reason [`Decision`] gives — the same reason the dialog's own "Patch Now" button
/// does.
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

    execute(policy, state, work, report, Duration::ZERO);
}

fn execute(policy: &PatchingPolicy, state: &mut ScheduleState, work: Plan, report: &StatusReporter, warning: Duration) {
    if !warning.is_zero() {
        let minutes = (warning.as_secs() + 59) / 60;
        let warning_message =
            format!("Patching will begin in {minutes} minute{}. Please save your work.", if minutes == 1 { "" } else { "s" });
        dialogs::notify("Kintsugi Patching", &warning_message);
        report(AgentStatus::Patching { current: warning_message, completed: 0, total: 0 });
        std::thread::sleep(warning);
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

/// What the confirm-or-delay dialog settled on.
///
/// Whether the five-minute warning comes first turns on one question: is patching starting because
/// a person just asked for it, or because this host's schedule ran out of patience? The warning is
/// notice before an *automatic* start, so a click on "Patch Now" — which is that notice, given by
/// the very person the interruption falls on — starts patching there and then.
///
/// The two delaying variants differ in what the delay costs. An explicit "Delay" buys a fresh
/// period from now; an unanswered dialog has already spent one waiting for the answer, so it
/// counts down the budget without postponing anything further. Kept identical in the other two
/// agents.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Decision {
    /// The user clicked "Patch Now": start immediately, with no warning and no further notice.
    PatchNow,
    /// There were no delays left to offer, so the acknowledgement has already been shown and
    /// patching proceeds. `remaining` is what is left of `WARNING_PERIOD` after however long that
    /// dialog stood there, which is what keeps the notice five minutes in total.
    ProceedAfterWarning { remaining: Duration },
    /// The user asked for more time. Nothing happens until the new due time arrives.
    Delayed,
    /// Nobody answered before the dialog gave up. Still a delay — the user was asked and said
    /// nothing — but the cycle is left due at once, so the next poll tick re-asks with the count
    /// decremented, and the budget running out lands on the branch above. See
    /// `ScheduleState::register_unanswered_prompt`.
    Unanswered,
}

impl Decision {
    /// Reads the dialog's answer as a decision. Split out from `confirm_or_delay` — which cannot
    /// be tested without a display — purely so the polarity of a timeout is pinned by a test
    /// rather than by a comment.
    fn from_choice(choice: ConfirmChoice) -> Self {
        match choice {
            // The click *is* the notice the warning exists to give, and it came from the one
            // person the interruption falls on — so patching starts now, as asked.
            ConfirmChoice::PatchNow => Decision::PatchNow,
            ConfirmChoice::Delay => Decision::Delayed,
            ConfirmChoice::TimedOut => Decision::Unanswered,
        }
    }
}

/// Asks whether this cycle proceeds, and on whose say-so — see [`Decision`] for what each answer
/// means and why an unanswered dialog is not a delay.
fn confirm_or_delay(
    policy: &PatchingPolicy,
    state: &mut ScheduleState,
    app_names: &[String],
    os_update_available: bool,
    report: &StatusReporter,
) -> anyhow::Result<Decision> {
    // Either dialog below stands there until it is answered, and the confirm one for a whole
    // delay period — so the menu is told a prompt is up, which greys its "Patch Now" item rather
    // than leaving it offering to ask a question that is on screen being asked. The cycle runs on
    // its own thread (`main::spawn_cycle`), so the scheduler is free to serve the menu meanwhile.
    report(AgentStatus::AwaitingAnswer);

    if !state.can_delay(policy) {
        // This dialog *is* the warning, not a preamble to it: it states the period and stands for
        // as much of it as the user leaves it up, and `execute` waits out whatever `remaining_warning`
        // says is left.
        let shown_at = schedule::now_epoch();
        dialogs::acknowledge(
            &format!(
                "The maximum number of delays has been used. Patching will begin in {} minutes — please save your work.",
                WARNING_PERIOD.as_secs() / 60
            ),
            WARNING_PERIOD.as_secs(),
        )?;
        let stood_for = Duration::from_secs(schedule::now_epoch().saturating_sub(shown_at));
        return Ok(Decision::ProceedAfterWarning { remaining: remaining_warning(stood_for) });
    }

    // Stamped before the dialog rather than after, because how long it stood there is what the
    // delay budget is charged for — including any of it the machine spent asleep.
    let shown_at = schedule::now_epoch();
    let choice = dialogs::confirm_patch(
        &policy.delay_label(),
        state.delays_remaining(policy),
        app_names,
        os_update_available,
        policy.delay_seconds(),
    )?;

    let decision = Decision::from_choice(choice);
    match decision {
        Decision::Delayed => {
            state.register_delay(policy);
            report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });
        }
        // Nothing is reported here: the cycle is due again immediately, so the next tick either
        // re-asks or finds the budget gone and proceeds. Telling the menu "next patch due: now"
        // for the few seconds in between would say less than the line already there.
        Decision::Unanswered => state.register_unanswered_prompt(policy, shown_at),
        Decision::PatchNow | Decision::ProceedAfterWarning { .. } => {}
    }

    Ok(decision)
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_notice_left_up_for_the_whole_period_leaves_nothing_to_wait_out() {
        assert_eq!(remaining_warning(WARNING_PERIOD), Duration::ZERO);
        assert_eq!(remaining_warning(WARNING_PERIOD * 2), Duration::ZERO, "and an overrun is not negative time");
    }

    #[test]
    fn a_notice_read_and_dismissed_leaves_the_rest_of_the_period_to_run() {
        assert_eq!(remaining_warning(Duration::from_secs(60)), WARNING_PERIOD - Duration::from_secs(60));
    }

    /// The common unattended case, where the dialog gives up a moment either side of the period:
    /// what is left is rounding, not notice.
    #[test]
    fn a_remainder_of_a_few_seconds_is_no_remainder_at_all() {
        assert_eq!(remaining_warning(WARNING_PERIOD - Duration::from_secs(5)), Duration::ZERO);
    }

    #[test]
    fn clicking_patch_now_starts_immediately_rather_than_after_the_warning() {
        assert_eq!(Decision::from_choice(ConfirmChoice::PatchNow), Decision::PatchNow);
    }

    /// The dialog's own giveup *is* one delay period, so an unanswered dialog has already spent
    /// the time a delay buys: it counts down the budget and is re-asked at once, rather than
    /// postponing by a second period on top — which is what made the count fall once every two.
    #[test]
    fn an_unanswered_dialog_spends_a_delay_without_postponing_the_cycle_again() {
        assert_eq!(Decision::from_choice(ConfirmChoice::TimedOut), Decision::Unanswered);
    }

    #[test]
    fn clicking_delay_defers_the_cycle() {
        assert_eq!(Decision::from_choice(ConfirmChoice::Delay), Decision::Delayed);
    }
}
