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
