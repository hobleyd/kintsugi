/// What the notification area's menu should currently show — pushed by the scheduler/patch-cycle
/// (which know nothing about the tray icon itself) via a plain callback, and turned into actual
/// menu text by `tray_menu`, the only module that talks to Win32. Structured (rather than a
/// pre-formatted string) so the menu can render its own compact progress line, independent of
/// whatever a balloon notification's text happens to look like.
#[derive(Debug, Clone)]
pub enum AgentStatus {
    Idle {
        next_due_epoch: u64,
    },
    Patching {
        /// What's being worked on right now — an application name (optionally "-> version"), or
        /// "Installing Windows updates", or the 5-minute warning message before anything starts.
        current: String,
        completed: usize,
        total: usize,
    },
}

pub type StatusReporter<'a> = dyn Fn(AgentStatus) + Send + Sync + 'a;

/// The menu's "Next check-in" line — the service's hourly schedule, which is a separate concern
/// from the patch cycle above: a check-in (registration, inventory, the agent's own update)
/// happens whether or not anything is due to be patched, and `AgentStatus::Patching` says nothing
/// about it. Reported through `tray_menu::report_check_in` by the scheduler thread, which is the
/// only thing that reads `checkin_schedule::next_check_in_epoch`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CheckInStatus {
    /// Waiting for the next hourly check-in. `None` until the service's first run has persisted
    /// this host's check-in minute — see `checkin_schedule::load_or_assign`.
    Scheduled { next_epoch: Option<u64> },
    /// A "Check In Now" request is with the service — see `checkin_schedule::request_now`.
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
