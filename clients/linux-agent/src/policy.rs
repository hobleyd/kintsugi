use std::fs;
use std::os::unix::fs::PermissionsExt;
use std::path::Path;
use std::time::{SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

use crate::config::Config;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum TimeUnit {
    Hours,
    Days,
}

impl TimeUnit {
    /// Matches Kintsugi.Domain.Enums.PatchingTimeUnit's member order (Hours = 0, Days = 1)
    /// — the API serializes it as that plain ordinal, not as a name, so this is a manual mapping
    /// rather than something `serde` derives directly.
    fn from_ordinal(value: u8) -> Self {
        if value == 1 {
            TimeUnit::Days
        } else {
            TimeUnit::Hours
        }
    }

    pub fn to_seconds(self, value: u32) -> u64 {
        let per_unit: u64 = match self {
            TimeUnit::Hours => 3600,
            TimeUnit::Days => 86400,
        };
        value as u64 * per_unit
    }

    pub fn label(self) -> &'static str {
        match self {
            TimeUnit::Hours => "hour(s)",
            TimeUnit::Days => "day(s)",
        }
    }
}

/// Mirrors the backend's `PatchingPolicySettingsDto` — see
/// Kintsugi.Application/PatchingPolicy/PatchingPolicySettingsDto.cs.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PatchingPolicy {
    pub interval_value: u32,
    pub interval_unit: TimeUnit,
    pub delay_value: u32,
    pub delay_unit: TimeUnit,
    pub max_delay_count: u32,
    /// When this copy was fetched (or last successfully refreshed) — not part of the API
    /// response; stamped locally so anyone reading the cache file (or the per-user process
    /// reading it after the root service wrote it) can tell how old the answer is.
    fetched_epoch: u64,
}

impl PatchingPolicy {
    pub fn interval_seconds(&self) -> u64 {
        self.interval_unit.to_seconds(self.interval_value)
    }

    pub fn delay_seconds(&self) -> u64 {
        self.delay_unit.to_seconds(self.delay_value)
    }

    pub fn delay_label(&self) -> String {
        format!("{} {}", self.delay_value, self.delay_unit.label())
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawPolicy {
    interval_value: u32,
    interval_unit: u8,
    delay_value: u32,
    delay_unit: u8,
    max_delay_count: u32,
}

fn now_epoch() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs()
}

/// Fetches the current patching policy from the backend and writes it to `cache_path` — both so a
/// later run that can't reach the server (network blip, VPN down) can fall back to the last known
/// policy rather than having none at all, and because that cache file is how the policy reaches
/// the per-user process at all. See `refresh` and `load_cached`.
///
/// Only ever called by the root service: `/api/patching-policy` is inside nginx's
/// client-certificate regex (see nginx/default.conf), so this needs the mutual-TLS identity, and
/// on this platform only root holds one.
fn fetch(client: &reqwest::blocking::Client, config: &Config, cache_path: &Path) -> Result<PatchingPolicy> {
    let response = client
        .get(config.patching_policy_url())
        .send()
        .context("request failed")?;

    if !response.status().is_success() {
        anyhow::bail!("request rejected (HTTP {})", response.status());
    }

    let raw = response.json::<RawPolicy>().context("could not parse response")?;
    let policy = PatchingPolicy {
        interval_value: raw.interval_value,
        interval_unit: TimeUnit::from_ordinal(raw.interval_unit),
        delay_value: raw.delay_value,
        delay_unit: TimeUnit::from_ordinal(raw.delay_unit),
        max_delay_count: raw.max_delay_count,
        fetched_epoch: now_epoch(),
    };

    if let Some(parent) = cache_path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    if let Ok(json) = serde_json::to_string_pretty(&policy) {
        let _ = fs::write(cache_path, json);
        // Explicitly, rather than left to root's umask: the per-user process reads this file, and
        // it is the only thing standing between that process and having no policy at all. It
        // carries no secret — the whole file is four intervals and a delay count.
        let _ = fs::set_permissions(cache_path, fs::Permissions::from_mode(0o644));
    }

    crate::logging::info(&format!(
        "fetched patching policy: every {} {}, delay {} {} (max {} delays)",
        policy.interval_value,
        policy.interval_unit.label(),
        policy.delay_value,
        policy.delay_unit.label(),
        policy.max_delay_count
    ));

    Ok(policy)
}

/// Reads the last policy the root service wrote to `cache_path`. This is the *only* way the
/// per-user process ever obtains a policy: it has no mutual-TLS identity of its own to fetch one
/// with (see `config::identity_dir` for why that's deliberate on Linux), so the root service
/// fetches on every check-in and this side reads what landed. Returns `None` until that first
/// successful fetch — the caller should keep polling.
///
/// Identical in shape to the Windows agent's `policy::load_cached`, and for the identical reason.
pub fn load_cached(cache_path: &Path) -> Option<PatchingPolicy> {
    let contents = fs::read_to_string(cache_path).ok()?;
    serde_json::from_str(&contents).ok()
}

/// Fetches a fresh policy and caches it, falling back to whatever was last cached on disk if the
/// fetch fails (e.g. the server is briefly unreachable) — patching scheduling should keep working
/// off the last known policy rather than stalling entirely over a transient network error. Called
/// only from the root service; returns `None` when there's neither a successful fetch nor any
/// usable cache yet (e.g. first run with no network).
///
/// Unconditional on every check-in rather than staleness-gated the way the Windows service's is:
/// that service is resident and loops far faster than the policy changes, while this one is a
/// systemd oneshot fired hourly, so "every invocation" already *is* the refresh interval.
pub fn refresh(client: &reqwest::blocking::Client, config: &Config, cache_path: &Path) -> Option<PatchingPolicy> {
    match fetch(client, config, cache_path) {
        Ok(policy) => Some(policy),
        Err(err) => {
            crate::logging::warn(&format!("could not fetch patching policy, using cached copy if any: {err:#}"));
            load_cached(cache_path)
        }
    }
}

#[cfg(test)]
impl PatchingPolicy {
    /// A policy with whatever intervals a test needs, stamped as freshly fetched. Only the fields
    /// the scheduling logic actually reads are settable — `fetched_epoch` is private precisely so
    /// nothing outside this module can forge a staleness answer.
    pub fn for_test(interval_hours: u32, delay_hours: u32, max_delay_count: u32) -> Self {
        Self {
            interval_value: interval_hours,
            interval_unit: TimeUnit::Hours,
            delay_value: delay_hours,
            delay_unit: TimeUnit::Hours,
            max_delay_count,
            fetched_epoch: now_epoch(),
        }
    }
}
