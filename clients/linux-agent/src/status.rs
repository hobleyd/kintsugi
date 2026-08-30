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
    Patching {
        /// What's being worked on right now — an application name (optionally "-> version"), or
        /// "Installing system updates", or the 5-minute warning message before anything starts.
        current: String,
        completed: usize,
        total: usize,
    },
}

pub type StatusReporter<'a> = dyn Fn(AgentStatus) + Send + Sync + 'a;
