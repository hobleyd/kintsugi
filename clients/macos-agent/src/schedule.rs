use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

use crate::policy::PatchingPolicy;

fn now_epoch() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs()
}

/// Persisted locally (per logged-in user — see `config::user_state_dir`) so scheduling survives
/// an agent restart, a log-out/log-in cycle, and — since this is just plain wall-clock
/// comparison, not a running timer — system sleep: the next check after waking naturally sees
/// however much real time actually passed, no separate wake-detection needed.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ScheduleState {
    next_due_epoch: u64,
    delay_count: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    last_completed_epoch: Option<u64>,
    #[serde(skip)]
    path: PathBuf,
}

impl ScheduleState {
    /// Loads previously persisted state, or starts fresh — a brand-new install isn't due
    /// immediately on first run, only after one full interval, so installing the agent doesn't
    /// itself trigger an immediate confirmation prompt.
    pub fn load_or_default(path: &Path, policy: &PatchingPolicy) -> Self {
        if let Some(mut state) = fs::read_to_string(path).ok().and_then(|c| serde_json::from_str::<Self>(&c).ok()) {
            state.path = path.to_path_buf();
            return state;
        }

        let state = Self {
            next_due_epoch: now_epoch() + policy.interval_seconds(),
            delay_count: 0,
            last_completed_epoch: None,
            path: path.to_path_buf(),
        };
        state.save();
        state
    }

    fn save(&self) {
        if let Some(parent) = self.path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        if let Ok(json) = serde_json::to_string_pretty(self) {
            if let Err(err) = fs::write(&self.path, json) {
                crate::logging::warn(&format!("could not save patching schedule state to {}: {err}", self.path.display()));
            }
        }
    }

    pub fn is_due(&self) -> bool {
        now_epoch() >= self.next_due_epoch
    }

    pub fn next_due_epoch(&self) -> u64 {
        self.next_due_epoch
    }

    pub fn can_delay(&self, policy: &PatchingPolicy) -> bool {
        self.delay_count < policy.max_delay_count
    }

    pub fn delays_remaining(&self, policy: &PatchingPolicy) -> u32 {
        policy.max_delay_count.saturating_sub(self.delay_count)
    }

    /// Postpones this due cycle by one delay period, without resetting `delay_count` — the same
    /// prompt (and its shrinking delay budget) resumes once the delay elapses, rather than
    /// starting a fresh set of delays.
    pub fn register_delay(&mut self, policy: &PatchingPolicy) {
        self.next_due_epoch = now_epoch() + policy.delay_seconds();
        self.delay_count += 1;
        self.save();
        crate::logging::info(&format!(
            "patching delayed by {} ({} of {} delays used); next due at epoch {}",
            policy.delay_label(),
            self.delay_count,
            policy.max_delay_count,
            self.next_due_epoch
        ));
    }

    /// Records a completed (or abandoned/failed — see the caller) patch cycle: resets the delay
    /// budget and schedules the next cycle a full interval out.
    pub fn register_completed(&mut self, policy: &PatchingPolicy) {
        let now = now_epoch();
        self.last_completed_epoch = Some(now);
        self.next_due_epoch = now + policy.interval_seconds();
        self.delay_count = 0;
        self.save();
        crate::logging::info(&format!("patch cycle completed; next due at epoch {}", self.next_due_epoch));
    }
}
