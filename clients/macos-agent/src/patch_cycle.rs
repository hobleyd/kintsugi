use std::time::Duration;

use crate::config::{self, Config};
use crate::dialogs::{self, ConfirmChoice};
use crate::identity::AgentIdentity;
use crate::logging;
use crate::os_update;
use crate::policy::PatchingPolicy;
use crate::queue::{self, RequestKind};
use crate::schedule::{self, ScheduleState};
use crate::status::{AgentStatus, StatusReporter};
use crate::upgrade::{self, UpgradeStatus};

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
/// Whether a person dismissed the "no delays left" acknowledgement, as against it giving up on its
/// own after `WARNING_PERIOD`.
///
/// Split out and tested for the same reason `Decision::from_choice` is: the polarity is the whole
/// value of it, and getting it backwards means a password dialog going up on an unattended Mac
/// every cycle — or, worse, never going up on one somebody is sitting at. `acknowledge` returns
/// `Ok(())` for both outcomes, so how long it stood there is the only signal there is.
fn acknowledged_by_a_person(stood_for: Duration) -> bool {
    stood_for < WARNING_PERIOD
}

fn remaining_warning(stood_for: Duration) -> Duration {
    let remaining = WARNING_PERIOD.saturating_sub(stood_for);
    if remaining < MINIMUM_WARNING {
        Duration::ZERO
    } else {
        remaining
    }
}

/// Everything a patch cycle would actually do, worked out up front — before showing any dialog —
/// so a dialog only ever appears when there's real work behind it. See `plan`.
struct PendingWork {
    apps: Vec<UpgradeStatus>,
    os_update_available: bool,
    /// The version the macOS update would bring this host to, when `softwareupdate -l` said. Named
    /// in both dialogs rather than only counted — see `dialogs::confirmation_message` — and sent to
    /// the server as the attempted version if the install fails.
    os_update_version: Option<String>,
    /// The labels that same `softwareupdate -l` offered, carried so `run_os_update` can ask whether
    /// the daemon's pre-fetch has staged them without running a second scan of its own.
    os_update_labels: Vec<String>,
}

impl PendingWork {
    /// The names the confirmation dialog lists, in the order they will be patched — see
    /// `dialogs::confirmation_message`.
    fn app_names(&self) -> Vec<String> {
        self.apps.iter().map(|app| app.application_name.clone()).collect()
    }

    /// Narrows this plan to the applications an administrator actually forced, and drops the OS
    /// update with it.
    ///
    /// Both halves matter. "Force this application" names one row on one screen, so a forced run
    /// that also installed every other pending patch — and a macOS update, which reboots — would do
    /// enormously more than was asked, in the one situation where the person asking is least able
    /// to absorb the surprise. And the names are matched case-insensitively because that is how
    /// applications are matched everywhere else in this agent and on the server.
    ///
    /// Kept identical in the other two agents.
    fn narrowed_to(self, application_names: &[String]) -> PendingWork {
        let apps = self
            .apps
            .into_iter()
            .filter(|app| application_names.iter().any(|name| name.eq_ignore_ascii_case(&app.application_name)))
            .collect();

        PendingWork { apps, os_update_available: false, os_update_version: None, os_update_labels: Vec::new() }
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

    let os_update = os_update::check().unwrap_or_else(|err| {
        logging::warn(&format!("could not check for macOS updates: {err:#}"));
        os_update::OsUpdateStatus::default()
    });

    Ok(PendingWork {
        apps,
        os_update_available: os_update.available,
        os_update_version: os_update.latest_version,
        os_update_labels: os_update.labels,
    })
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

    let (warning, user_present) = match confirm_or_delay(
        policy,
        state,
        &work.app_names(),
        work.os_update_available,
        work.os_update_version.as_deref(),
        report,
    ) {
        Ok(Decision::PatchNow) => (Duration::ZERO, true),
        Ok(Decision::ProceedAfterWarning { remaining, acknowledged }) => (remaining, acknowledged),
        // Both delaying answers stop here: `confirm_or_delay` has already moved the due time, and
        // the next tick picks the cycle back up — at once for an unanswered dialog, a delay period
        // later for an explicit "Delay".
        Ok(Decision::Delayed) | Ok(Decision::Unanswered) => return,
        Err(err) => {
            logging::warn(&format!("could not show the patching confirmation dialog, will retry at the next check: {err:#}"));
            return;
        }
    };

    execute(client, config, serial_number, policy, state, work, identity, report, warning, true, user_present);
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

    // The click came from the menu bar, so somebody is unambiguously at this Mac.
    execute(client, config, serial_number, policy, state, work, identity, report, Duration::ZERO, true, true);
}

/// An administrator's "patch this now", collected from the server by the scheduler loop — see
/// `forced_patch_run::collect`. This is the emergency path: a named application is patched on this
/// host at the next poll tick rather than at this host's next scheduled cycle.
///
/// It is the third entry point rather than a flag on one of the other two, because it answers the
/// confirm/delay question and the warning question differently from both:
///
/// - **No confirm/delay dialog.** That dialog exists to let the person at the desk move an
///   *automatic* cycle out of the way. An administrator forcing a run has already decided the
///   emergency outranks the interruption, so offering "Delay" would be offering something the
///   answer to has already been given — which is the whole of what was asked for here.
/// - **The full five-minute warning, all the same.** `run_now` skips it because the click came from
///   the very person the interruption falls on (see [`Decision`]); that reasoning does not hold
///   here, where the person deciding is somewhere else entirely. So the host gets the same notice a
///   scheduled cycle gives — "Patching will begin in 5 minutes. Please save your work." — with
///   nothing to click.
/// - **The schedule is not touched.** A forced run patches one application; the scheduled cycle
///   patches everything. Registering this as a completed cycle would push the real one a whole
///   interval into the future, so an emergency patch would silently cost this host its next
///   ordinary one. `execute`'s `reschedule` argument is what says so.
///
/// Kept identical in the other two agents.
pub fn run_forced(
    client: &reqwest::blocking::Client,
    config: &Config,
    policy: &PatchingPolicy,
    state: &mut ScheduleState,
    serial_number: &str,
    identity: &AgentIdentity,
    report: &StatusReporter,
    application_names: &[String],
) {
    logging::info(&format!("running a forced patch cycle for: {}", application_names.join(", ")));

    let work = match plan(client, config, serial_number, identity) {
        Ok(work) => work,
        Err(err) => {
            // Nothing is lost by giving up quietly here *except* the instruction itself: the server
            // has already marked it collected, so the next tick will not bring it back. Said at
            // warn level for that reason — this is the one branch where "try again later" is not
            // true and an administrator may be waiting on a patch that is not coming.
            logging::warn(&format!("could not check what to patch for a forced run, so it has been dropped: {err:#}"));
            return;
        }
    };

    let work = work.narrowed_to(application_names);

    if work.is_empty() {
        // Not a failure, and deliberately not a notification: the usual cause is that the host is
        // already current on the application, or that its script is unsigned and therefore not
        // runnable at all (`upgrade::is_patchable`). Neither is something to interrupt the person
        // at this desk about for an instruction they did not raise.
        logging::info("forced patch run has nothing to do on this host — nothing patchable matched");
        return;
    }

    // `narrowed_to` has already dropped the OS update from a forced run, so the one step that
    // would ask a human for anything cannot be reached here.
    execute(client, config, serial_number, policy, state, work, identity, report, WARNING_PERIOD, false, false);
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
    warning: Duration,
    // Whether finishing counts as this host's scheduled patch cycle. True for the two whole-host
    // cycles; false for a forced run, which patches one named application and must not push the
    // real cycle an interval into the future — see `run_forced`.
    reschedule: bool,
    // Whether a human answered the dialog that got us here, rather than it giving up on its own.
    // Only the macOS authorization prompt reads it — see `run_patches`.
    user_present: bool,
) {
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

    let (succeeded, failed) = run_patches(client, config, serial_number, work, identity, report, user_present);

    let summary = if failed == 0 {
        format!("Patching complete — {succeeded} item(s) updated.")
    } else {
        format!("Patching finished with issues — {succeeded} succeeded, {failed} failed. Check the logs.")
    };
    dialogs::notify("Kintsugi Patching", &summary);
    logging::info(&format!("patch cycle finished: {summary}"));

    if reschedule {
        state.register_completed(policy);
    }
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
    ///
    /// `acknowledged` is whether somebody actually clicked OK, as against the dialog giving up on
    /// its own — the difference between a person at the keyboard and an empty desk. Patching
    /// proceeds either way (that is what spending the delay budget means), but a step that can only
    /// be completed by a human, like authorizing a macOS install, has nothing to gain from asking
    /// when nobody is there. See `run_patches`.
    ProceedAfterWarning { remaining: Duration, acknowledged: bool },
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
    os_update_version: Option<&str>,
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
        return Ok(Decision::ProceedAfterWarning {
            remaining: remaining_warning(stood_for),
            acknowledged: acknowledged_by_a_person(stood_for),
        });
    }

    // Stamped before the dialog rather than after, because how long it stood there is what the
    // delay budget is charged for — including any of it the machine spent asleep.
    let shown_at = schedule::now_epoch();
    let choice = dialogs::confirm_patch(
        &policy.delay_label(),
        state.delays_remaining(policy),
        app_names,
        os_update_available,
        os_update_version,
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
    user_present: bool,
) -> (usize, usize) {
    let PendingWork { apps, os_update_available, os_update_version, os_update_labels } = work;
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

        // Which side reports this row's result to the server, success or failure: whichever one
        // actually runs the script. See `upgrade::report_patch_failure`.
        let ran_by_the_daemon = upgrade::runs_as_root(app);

        let outcome = if ran_by_the_daemon {
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

                // Only for what this process ran itself. A daemon-run row's failure is reported by
                // the daemon (see `main::DaemonRequestHandler::patch_application`), which is the
                // side that saw the script fail and holds its full output; reporting it here as
                // well would record every root-run failure twice.
                if !ran_by_the_daemon {
                    upgrade::report_patch_failure(client, config, serial_number, app, &err);
                }
            }
        }
        completed += 1;
    }

    if os_update_available {
        let (os_succeeded, os_failed) =
            run_os_update(os_update_version.as_deref(), &os_update_labels, user_present, completed, total, report);
        succeeded += os_succeeded;
        failed += os_failed;
    }

    (succeeded, failed)
}

/// How long the "you still need to restart" notice stands before dismissing itself. It is
/// information, not a decision, so it must never block the rest of a cycle waiting to be clicked.
const RESTART_NOTICE_TIMEOUT: Duration = Duration::from_secs(120);

/// Whether the macOS update may go ahead at all, and who would have to authorize the install.
enum OsUpdateEligibility {
    /// Proceed with no credentials. Intel, where `softwareupdate` needs none and would reject the
    /// flags that carry them.
    NoAuthorizationNeeded,
    /// Proceed, but the install half needs this account's password.
    NeedsAuthorizationFrom(String),
    /// Do not attempt it at all, for the reason given.
    Skip(String),
}

/// Everything that can rule the macOS update out **before a single byte is downloaded**.
///
/// All of it is cheap — a `sysctl`, a `dscl` read and a `diskutil` read — and all of it used to be
/// discovered at the far end of a multi-gigabyte fetch instead:
///
/// - **Nobody there.** A cycle reaches the patching step unattended whenever the delay budget ran
///   out with nobody at the desk; that is what spending the budget is *for*. Downloading gigabytes
///   and then putting up a password dialog nobody will answer helps no one.
/// - **An account that is not a volume owner.** Being in `admin` is *not* the same thing: an account
///   created by MDM, or migrated onto Apple silicon, can be an administrator with no secure token.
///   `--user` names "an owner user"; anything else fails with `Failed to authenticate`, and only
///   after the download. A check that could not run is treated as a refusal, not as permission.
fn os_update_eligibility(user_present: bool) -> OsUpdateEligibility {
    if !os_update::is_apple_silicon() {
        return OsUpdateEligibility::NoAuthorizationNeeded;
    }

    if !user_present {
        return OsUpdateEligibility::Skip(
            "nobody answered the confirmation dialog, so there would be nobody to authorize the install".to_string(),
        );
    }

    let Some(username) = config::console_username() else {
        return OsUpdateEligibility::Skip("could not determine which account is logged in".to_string());
    };

    match os_update::is_volume_owner(&username) {
        Ok(true) => OsUpdateEligibility::NeedsAuthorizationFrom(username),
        Ok(false) => OsUpdateEligibility::Skip(format!(
            "'{username}' is not a volume owner of the boot volume, so macOS will not let it authorize a \
             system update — an administrator who is one has to install this, or grant it a secure token"
        )),
        Err(err) => OsUpdateEligibility::Skip(format!("could not tell whether '{username}' is a volume owner: {err:#}")),
    }
}

/// Asks for authorization and installs, and returns `(succeeded, failed)` for the cycle's tally.
///
/// **The download is not here any more.** It used to be step one: a `RequestKind::OsDownload`
/// submitted to the daemon and waited on, so that the install that followed found the assets on
/// disk and the forced restart landed minutes after the person agreed to it rather than an hour and
/// a half. The ordering was right and still is — what was wrong was doing it *inside the cycle*.
/// `MenuState::refresh_actions` disables "Check In Now" and "Patch Now" for as long as a cycle
/// runs, so fetching this host's 15GB of pending updates left the menu bar dead for hours, showing
/// "Downloading the macOS update", with no way to check in and no sign it was not simply hung. It
/// is root-only work that needs no authorization to start and has no user waiting on it, so it now
/// happens on the daemon's own check-in — `main::prefetch_os_updates`.
///
/// What arrives here instead is the consequence: **this step declines to run at all until the bits
/// are staged.** Prompting first and downloading afterwards is the thing the split exists to
/// prevent, so a cycle that finds nothing staged says so and leaves it for the pre-fetch, which the
/// daemon runs hourly. The record it reads is the agent's own — macOS has no answer to "are the
/// bits here?" (see `os_update::StagedDownloads`) — and being wrong about it costs an install that
/// downloads, which is where this started rather than anywhere worse.
///
/// A prompt that goes unanswered is not wasted work either: the assets stay staged, so the next
/// cycle comes straight back here.
fn run_os_update(
    version: Option<&str>,
    offered: &[String],
    user_present: bool,
    completed: usize,
    total: usize,
    report: &StatusReporter,
) -> (usize, usize) {
    let username = match os_update_eligibility(user_present) {
        OsUpdateEligibility::NoAuthorizationNeeded => None,
        OsUpdateEligibility::NeedsAuthorizationFrom(username) => Some(username),
        // Not counted as a failure: nothing was attempted, and a Failed Updates row every cycle for
        // a Mac whose owner keeps declining would bury the failures that can be fixed.
        OsUpdateEligibility::Skip(reason) => {
            logging::info(&format!("skipping the macOS update this cycle: {reason}"));
            return (0, 0);
        }
    };

    // 1. Are the bits here? Not counted as a failure — nothing was attempted, the daemon's
    // pre-fetch is what fetches them, and a Failed Updates row every hour for a host that is simply
    // still downloading would bury the failures somebody can act on.
    if !os_update_is_staged(offered) {
        logging::info(
            "skipping the macOS install this cycle: the update has not been pre-fetched yet — \
             the daemon fetches it on its own check-in, and the next cycle will find it staged",
        );
        return (0, 0);
    }

    // 2. Authorize — with the bits already on disk, so the restart follows closely.
    let auth = match username {
        None => None,
        Some(username) => match dialogs::request_install_password(&username, version, PASSWORD_PROMPT_TIMEOUT.as_secs()) {
            Ok(dialogs::PasswordAnswer::Provided(password)) => Some(os_update::InstallAuth { user: username, password }),
            Ok(dialogs::PasswordAnswer::Cancelled) => {
                logging::info(&format!("skipping the macOS install this cycle: {username} declined to authorize it"));
                return (0, 0);
            }
            Ok(dialogs::PasswordAnswer::TimedOut) => {
                logging::info("skipping the macOS install this cycle: nobody answered the authorization prompt");
                return (0, 0);
            }
            Err(err) => {
                logging::info(&format!("skipping the macOS install this cycle: could not ask for authorization: {err:#}"));
                return (0, 0);
            }
        },
    };

    // 3. Install, and restart. This is the step that does not come back.
    let current = "Installing the macOS update — this Mac will restart".to_string();
    dialogs::notify("Kintsugi Patching", &format!("{current}\n{}", dialogs::progress_bar(completed, total)));
    report(AgentStatus::Patching { current, completed, total });

    logging::info("asking the root daemon to install the macOS updates and restart");
    match queue::submit_with_auth(&config::queue_dir(), RequestKind::OsUpdate, "", auth.as_ref()) {
        Ok(result) if result.success => {
            logging::info(&format!("macOS updates installed: {}", result.output.trim()));
            // Reaching here at all means the daemon's `-R` did *not* reboot us — the ordinary macOS
            // case never returns, because the restart happens inside `softwareupdate`. So this is
            // the leftover: something is still pending and nothing will restart on its own, which
            // the person who just authorized it needs telling.
            if result.output.contains(os_update::RESTART_REQUIRED_MARKER) {
                let _ = dialogs::acknowledge(
                    "The macOS update has been installed, but this Mac did not restart on its own. \
                     Restart it when convenient to finish applying the update.",
                    RESTART_NOTICE_TIMEOUT.as_secs(),
                );
            }
            (1, 0)
        }
        Ok(result) => {
            logging::error(&format!("macOS update install failed: {}", result.output.trim()));
            (0, 1)
        }
        Err(err) => {
            logging::error(&format!("could not install the macOS updates: {err:#}"));
            (0, 1)
        }
    }
}

/// Whether the daemon's pre-fetch has left every pending macOS update on disk.
///
/// Reads the daemon's own record rather than asking macOS, which has no answer — see
/// `os_update::StagedDownloads`. Anything unreadable counts as "not staged": declining to prompt
/// costs a cycle, and prompting wrongly costs the console user an hour between their password and
/// the reboot it authorizes.
fn os_update_is_staged(offered: &[String]) -> bool {
    os_update::read_staged(&config::os_download_state_path())
        .is_some_and(|staged| staged.covers_the_macos_updates(offered))
}

/// How long the authorization prompt stands before giving up.
///
/// Shorter than the patch confirmation's delay period on purpose: by this point the user has
/// already clicked "Patch Now" (or spent their delay budget) and the applications are being
/// installed, so this prompt appears while they are watching. Ten minutes is generous for somebody
/// who is there and short enough that an empty desk does not stall the rest of the cycle.
const PASSWORD_PROMPT_TIMEOUT: Duration = Duration::from_secs(10 * 60);

/// Asks the root daemon to run this application's upgrade — by name only; the daemon fetches and
/// verifies the script itself, see `queue`. The daemon's own log has the script's full output; what
/// comes back here is its verdict and last word.
fn patch_via_daemon(app: &UpgradeStatus) -> anyhow::Result<()> {
    let result = queue::submit(&config::queue_dir(), RequestKind::AppPatch, &app.application_name)?;
    if !result.success {
        anyhow::bail!("the root daemon reported: {}", result.output.trim());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn app(name: &str) -> UpgradeStatus {
        UpgradeStatus {
            application_name: name.to_string(),
            installed_version: "1.0".to_string(),
            latest_version: Some("2.0".to_string()),
            update_available: true,
            method: crate::upgrade::UpgradeMethod::Script,
            application_identifier: Some("com.example.app".to_string()),
            command: None,
            notes: None,
            script: Some("#!/bin/bash\n".to_string()),
            script_signature: None,
            command_signature: None,
            package_manager: None,
        }
    }

    /// The two properties a forced run rests on, and the only place either is enforced: it patches
    /// what was named and nothing else, and it never installs an OS update. `execute` and
    /// `run_patches` both read `os_update_available` off the plan, so clearing it here is what
    /// stops a forced run rebooting a machine over one application. Pinned in all three agents.
    /// The signal that decides whether a macOS authorization prompt is worth putting up at all.
    #[test]
    fn acknowledged_by_a_person_distinguishes_a_click_from_an_abandoned_dialog() {
        assert!(acknowledged_by_a_person(Duration::from_secs(4)), "read and dismissed at once");
        assert!(acknowledged_by_a_person(WARNING_PERIOD - Duration::from_secs(1)), "dismissed with a second to spare");
        assert!(!acknowledged_by_a_person(WARNING_PERIOD), "AppleScript gave up: nobody is there");
        // The machine slept with the dialog up, so it stood for longer than it was told to.
        assert!(!acknowledged_by_a_person(WARNING_PERIOD * 3));
    }

    #[test]
    fn narrowing_keeps_only_the_named_applications_and_drops_the_os_update() {
        let work = PendingWork { apps: vec![app("Firefox"), app("GIMP")], os_update_available: true, os_update_version: Some("26.7".to_string()), os_update_labels: vec!["macOS Tahoe 26.7-25G229".to_string()] };

        let narrowed = work.narrowed_to(&["gimp".to_string()]);

        assert_eq!(narrowed.app_names(), vec!["GIMP".to_string()], "matched case-insensitively");
        assert!(!narrowed.os_update_available, "a forced run patches one application, never the OS");
        assert_eq!(narrowed.total(), 1);
    }

    #[test]
    fn narrowing_to_something_this_host_cannot_patch_leaves_nothing_to_do() {
        let work = PendingWork { apps: vec![app("Firefox")], os_update_available: true, os_update_version: Some("26.7".to_string()), os_update_labels: vec!["macOS Tahoe 26.7-25G229".to_string()] };

        assert!(work.narrowed_to(&["LibreOffice".to_string()]).is_empty());
    }

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
