use std::time::Duration;

use crate::config::{self, Config};
use crate::dialogs::{self, ConfirmChoice};
use crate::identity::AgentIdentity;
use crate::logging;
use crate::os_update;
use crate::policy::PatchingPolicy;
use crate::queue::{self, RequestKind};
use crate::schedule::ScheduleState;
use crate::status::{AgentStatus, StatusReporter};
use crate::upgrade::{self, UpgradeStatus};

const WARNING_PERIOD: Duration = Duration::from_secs(5 * 60);

/// Everything a patch cycle would actually do, worked out up front — before showing any dialog —
/// so a dialog only ever appears when there's real work behind it. See `plan`.
struct PendingWork {
    apps: Vec<UpgradeStatus>,
    os_update_available: bool,
}

impl PendingWork {
    /// The names the confirmation dialog lists, in the order they will be patched — see
    /// `dialogs::confirmation_message`.
    fn app_names(&self) -> Vec<String> {
        self.apps.iter().map(|app| app.application_name.clone()).collect()
    }

    fn total(&self) -> usize {
        self.apps.len() + usize::from(self.os_update_available)
    }

    fn is_empty(&self) -> bool {
        self.total() == 0
    }
}

/// Fetches the current work list from the backend and checks for a macOS update. Kept separate
/// from, and always run *before*, any dialog — a confirm prompt (or the "no delays left"
/// acknowledgement) has no business appearing if the server can't even be reached to say whether
/// there's anything to patch, or if it says there isn't.
fn plan(client: &reqwest::blocking::Client, config: &Config, serial_number: &str, identity: &AgentIdentity) -> anyhow::Result<PendingWork> {
    let statuses = upgrade::fetch_upgrade_statuses(client, config, serial_number)?;
    let apps: Vec<_> = statuses.into_iter().filter(|status| upgrade::is_patchable(status, identity)).collect();

    let os_update_available = os_update::check_available().unwrap_or_else(|err| {
        logging::warn(&format!("could not check for macOS updates: {err:#}"));
        false
    });

    Ok(PendingWork { apps, os_update_available })
}

/// Runs one full due patch cycle: check what's actually pending, confirm (or delay) only if
/// there's real work, a 5-minute warning unless the user asked for patching to start now,
/// applications, then the OS if an update is available — in that order, per the policy. `report` is how the menu bar (which this module knows nothing
/// about) is kept in sync with what's happening.
///
/// Returns without doing anything destructive if the server can't be reached, if there's nothing
/// to patch, or if the user chooses to delay — in every case, the next poll tick (or the next due
/// time) picks it back up; none of these are treated as failures.
pub fn run(
    client: &reqwest::blocking::Client,
    config: &Config,
    policy: &PatchingPolicy,
    state: &mut ScheduleState,
    serial_number: &str,
    identity: &AgentIdentity,
    report: &StatusReporter,
) {
    let work = match plan(client, config, serial_number, identity) {
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

    let show_warning = match confirm_or_delay(policy, state, &work.app_names(), work.os_update_available, report) {
        Ok(Decision::PatchNow) => false,
        Ok(Decision::ProceedAfterWarning) => true,
        Ok(Decision::Delayed) => return, // nothing more to do until the new due time arrives
        Err(err) => {
            logging::warn(&format!("could not show the patching confirmation dialog, will retry at the next check: {err:#}"));
            return;
        }
    };

    execute(client, config, serial_number, policy, state, work, identity, report, show_warning);
}

/// The menu bar's "Patch Now" button: skips the confirm/delay decision altogether, since asking
/// whether to delay makes no sense when the user just explicitly asked to patch right now, and
/// goes straight into patching once it's confirmed there's actually something to do. It also
/// skips the 5-minute warning, for the reason [`Decision`] gives — the same reason the dialog's
/// own "Patch Now" button does.
pub fn run_now(
    client: &reqwest::blocking::Client,
    config: &Config,
    policy: &PatchingPolicy,
    state: &mut ScheduleState,
    serial_number: &str,
    identity: &AgentIdentity,
    report: &StatusReporter,
) {
    logging::info("Patch Now triggered manually from the menu bar");

    let work = match plan(client, config, serial_number, identity) {
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

    execute(client, config, serial_number, policy, state, work, identity, report, false);
}

fn execute(
    client: &reqwest::blocking::Client,
    config: &Config,
    serial_number: &str,
    policy: &PatchingPolicy,
    state: &mut ScheduleState,
    work: PendingWork,
    identity: &AgentIdentity,
    report: &StatusReporter,
    show_warning: bool,
) {
    if show_warning {
        let warning_message = format!("Patching will begin in {} minutes. Please save your work.", WARNING_PERIOD.as_secs() / 60);
        dialogs::notify("Kintsugi Patching", &warning_message);
        report(AgentStatus::Patching { current: warning_message, completed: 0, total: 0 });
        std::thread::sleep(WARNING_PERIOD);
    }

    dialogs::notify("Kintsugi Patching", "Patching has started — do not turn off your computer.");
    logging::info("patch cycle starting");

    let (succeeded, failed) = run_patches(client, config, serial_number, work, identity, report);

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

/// What the confirm-or-delay dialog settled on, from `execute`'s point of view.
///
/// The two proceeding variants differ only in whether the five-minute warning comes first, and
/// that turns on one question: is patching starting because a person just asked for it, or
/// because this host's schedule came round? The warning is notice before an *automatic* start, so
/// a click on "Patch Now" — which is that notice, given by the very person the interruption falls
/// on — starts patching there and then. Kept identical in the other two agents.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Decision {
    /// The user clicked "Patch Now": start immediately, with no warning and no further notice.
    PatchNow,
    /// Patching is starting on the schedule's say-so rather than the user's — the dialog timed
    /// out, or there were no delays left to offer. Warn first.
    ProceedAfterWarning,
    /// The user asked for more time. Nothing happens until the new due time arrives.
    Delayed,
}

impl Decision {
    /// Reads the dialog's answer as a decision. Split out from `confirm_or_delay` — which cannot
    /// be tested without a display — purely so the polarity of a timeout is pinned by a test
    /// rather than by a comment: it proceeds, and the only thing it costs is the warning.
    fn from_choice(choice: ConfirmChoice) -> Self {
        match choice {
            // The click *is* the notice the warning exists to give, and it came from the one
            // person the interruption falls on — so patching starts now, as asked.
            ConfirmChoice::PatchNow => Decision::PatchNow,
            // Nobody answered for a whole delay period, so the delay this dialog was offering has
            // already been spent in wall-clock time; deferring for another one would only re-ask a
            // question nobody is there to answer. No delay is registered, because the cycle is not
            // being deferred — it proceeds, with the warning any unattended start gets.
            ConfirmChoice::TimedOut => Decision::ProceedAfterWarning,
            ConfirmChoice::Delay => Decision::Delayed,
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
    if !state.can_delay(policy) {
        dialogs::acknowledge(
            "The maximum number of delays has been used — patching will now proceed.",
            WARNING_PERIOD.as_secs(),
        )?;
        // An acknowledgement is not a request to start now — it is notice that the budget is
        // gone, with nothing left to choose — so the five-minute warning still applies.
        return Ok(Decision::ProceedAfterWarning);
    }

    let choice = dialogs::confirm_patch(
        &policy.delay_label(),
        state.delays_remaining(policy),
        app_names,
        os_update_available,
        policy.delay_seconds(),
    )?;

    let decision = Decision::from_choice(choice);
    if decision == Decision::Delayed {
        state.register_delay(policy);
        report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });
    }

    Ok(decision)
}

/// Applications first, then the OS — per the policy's intent, application updates are the
/// frequent, low-risk case, while an OS update is the more disruptive one (likely to need a
/// restart) best left until everything else is already current. Returns (succeeded, failed)
/// counts across both.
///
/// Two of the three steps here run in the root daemon rather than this process — the OS update as
/// always, and every AI-researched application script since `upgrade::runs_as_root` (a root-owned
/// `/Applications` bundle cannot be replaced by the logged-in user). The daemon reports those
/// results to the server itself, since it is the side that knows they succeeded; this process
/// reports only what it ran, which is Homebrew.
fn run_patches(
    client: &reqwest::blocking::Client,
    config: &Config,
    serial_number: &str,
    work: PendingWork,
    identity: &AgentIdentity,
    report: &StatusReporter,
) -> (usize, usize) {
    let PendingWork { apps, os_update_available } = work;
    let total = apps.len() + usize::from(os_update_available);
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

        let outcome = if upgrade::runs_as_root(app) {
            logging::info(&format!(
                "attempting to patch {} (method {:?}) via the root daemon",
                app.application_name, app.method
            ));
            patch_via_daemon(app)
        } else {
            logging::info(&format!("attempting to patch {} (method {:?})", app.application_name, app.method));
            upgrade::patch_one(app, identity).map(|()| {
                match &app.latest_version {
                    Some(new_version) => upgrade::report_patch_result(client, config, serial_number, &app.application_name, new_version),
                    None => logging::warn(&format!(
                        "patched {} successfully, but no latest_version was known to report to the server",
                        app.application_name
                    )),
                }
            })
        };

        match outcome {
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
        let current = "Installing macOS updates — this may take a while".to_string();
        dialogs::notify("Kintsugi Patching", &format!("{current}\n{}", dialogs::progress_bar(completed, total)));
        report(AgentStatus::Patching { current, completed, total });

        logging::info("attempting to install macOS updates via the root daemon");
        match queue::submit(&config::queue_dir(), RequestKind::OsUpdate, "", queue::REQUEST_TIMEOUT) {
            Ok(result) if result.success => {
                succeeded += 1;
                logging::info("macOS updates installed successfully");
            }
            Ok(result) => {
                failed += 1;
                logging::error(&format!("macOS update install failed: {}", result.output.trim()));
            }
            Err(err) => {
                failed += 1;
                logging::error(&format!("could not install macOS updates: {err:#}"));
            }
        }
    }

    (succeeded, failed)
}

/// Asks the root daemon to run this application's upgrade — by name only; the daemon fetches and
/// verifies the script itself, see `queue`. The daemon's own log has the script's full output; what
/// comes back here is its verdict and last word.
fn patch_via_daemon(app: &UpgradeStatus) -> anyhow::Result<()> {
    let result = queue::submit(&config::queue_dir(), RequestKind::AppPatch, &app.application_name, queue::REQUEST_TIMEOUT)?;
    if !result.success {
        anyhow::bail!("the root daemon reported: {}", result.output.trim());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clicking_patch_now_starts_immediately_rather_than_after_the_warning() {
        assert_eq!(Decision::from_choice(ConfirmChoice::PatchNow), Decision::PatchNow);
    }

    /// The dialog's own timeout *is* one delay period, so an unanswered dialog has already had
    /// the time a delay would have bought it. It proceeds — after the warning, which is the only
    /// notice anybody gets when nobody was there to be asked.
    #[test]
    fn an_unanswered_dialog_proceeds_after_the_warning_rather_than_delaying_again() {
        assert_eq!(Decision::from_choice(ConfirmChoice::TimedOut), Decision::ProceedAfterWarning);
    }

    #[test]
    fn clicking_delay_defers_the_cycle() {
        assert_eq!(Decision::from_choice(ConfirmChoice::Delay), Decision::Delayed);
    }
}
