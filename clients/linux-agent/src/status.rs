/// What the notification-area icon's menu should currently show — pushed by the
/// scheduler/patch-cycle (which know nothing about the icon itself) via a plain callback, and
/// turned into actual menu text by `tray_menu`, which is the only module that knows about `ksni`.
/// Structured (rather than a pre-formatted string) so the menu can render its own compact
/// progress line, independent of whatever a notification banner's text happens to look like.
#[derive(Debug, Clone)]
pub enum AgentStatus {
    Idle {
        next_due_epoch: u64,
    },
    /// A dialog is on screen and the cycle is waiting for the person at the keyboard to answer it
    /// — the confirm-or-delay prompt, or the "no delays left" notice. Its own state rather than a
    /// flavour of `Patching`, because nothing is being patched yet and the progress window would
    /// be asserting otherwise; what it shares with `Patching` is that the menu's actions are
    /// greyed, since the question the "Patch Now" item asks is already on screen being asked.
    AwaitingAnswer,
    Patching {
        /// What's being worked on right now — an application name (optionally "-> version"), or
        /// "Installing system updates", or the 5-minute warning message before anything starts.
        current: String,
        completed: usize,
        total: usize,
    },
}

pub type StatusReporter<'a> = dyn Fn(AgentStatus) + Send + Sync + 'a;

/// The reporter as the scheduler actually holds it: a plain function pointer, because a patch
/// cycle runs on its own thread (see `main::spawn_cycle`) and the reporter has to be something
/// that can be copied into it — `tray_menu::report_status` is a `fn`, not a closure. Nothing below
/// the scheduler notices: `patch_cycle` still takes `&StatusReporter`, which a `fn` coerces to.
pub type StatusReporterFn = fn(AgentStatus);

/// The menu's "Next check-in" line — the root service's hourly schedule, which is a separate
/// concern from the patch cycle above: a check-in (registration, inventory, the agent's own update)
/// happens whether or not anything is due to be patched, and `AgentStatus::Patching` says nothing
/// about it. Reported through `tray_menu::report_check_in` by the scheduler thread, which is the
/// only thing that reads `checkin_schedule::next_check_in_epoch`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CheckInStatus {
    /// Waiting for the next hourly check-in. `None` until the root service's first run has
    /// persisted this host's check-in minute — see `checkin_schedule::load_or_assign`.
    Scheduled { next_epoch: Option<u64> },
    /// A "Check In Now" request is with the root service — see `checkin_schedule::request_now`.
    InProgress,
}

/// What a click on one of the menu's action items asks the scheduler thread to do. One channel
/// carries both so the scheduler serves them in the order they were clicked and never two at once.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MenuAction {
    /// "Patch Now" — see `patch_cycle::run_now`.
    PatchNow,
    /// "Check In Now" — see `checkin_schedule::request_now`.
    CheckInNow,
}
