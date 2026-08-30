use std::fs;
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
    /// response; stamped locally so a stale cache can be told apart from a fresh fetch.
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
/// policy rather than having none at all, and because that cache file is how the policy reaches the
/// tray process at all. See `refresh` and `load_cached`.
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

/// Reads the last policy the service wrote to `cache_path`. This is the *only* way the tray
/// process ever obtains a policy: it has no mutual-TLS identity of its own to fetch one with (see
/// `config::identity_dir` for why that's deliberate on Windows), so the service fetches on every
/// check-in and this side reads what landed. Returns `None` until the service's first successful
/// fetch — the caller should keep polling.
pub fn load_cached(cache_path: &Path) -> Option<PatchingPolicy> {
    let contents = fs::read_to_string(cache_path).ok()?;
    serde_json::from_str(&contents).ok()
}

/// Fetches a fresh policy and caches it, falling back to whatever was last cached if the fetch
/// fails (e.g. the server is briefly unreachable) — patching scheduling should keep working off the
/// last known policy rather than stalling entirely over a transient network error. Called only from
/// the service; returns `None` when there's neither a successful fetch nor any usable cache yet
/// (e.g. first run with no network).
pub fn refresh(client: &reqwest::blocking::Client, config: &Config, cache_path: &Path) -> Option<PatchingPolicy> {
    match fetch(client, config, cache_path) {
        Ok(policy) => Some(policy),
        Err(err) => {
            crate::logging::warn(&format!("could not fetch patching policy, using cached copy if any: {err:#}"));
            load_cached(cache_path)
        }
    }
}

/// Whether a cached policy is old enough to be worth refreshing — the service's check-in loop
/// checks this on every tick but only actually re-fetches occasionally, since the policy changes
/// rarely.
pub fn is_stale(policy: &PatchingPolicy, max_age_seconds: u64) -> bool {
    now_epoch().saturating_sub(policy.fetched_epoch) >= max_age_seconds
}

#[cfg(test)]
mod tests {
    use super::*;

    fn policy_fetched(seconds_ago: u64) -> PatchingPolicy {
        PatchingPolicy {
            interval_value: 1,
            interval_unit: TimeUnit::Days,
            delay_value: 2,
            delay_unit: TimeUnit::Hours,
            max_delay_count: 3,
            fetched_epoch: now_epoch().saturating_sub(seconds_ago),
        }
    }

    #[test]
    fn time_unit_from_ordinal_matches_the_backends_member_order() {
        // PatchingTimeUnit serializes as a plain ordinal, so an off-by-one here would silently turn
        // a 1-hour patching interval into a 1-day one.
        assert_eq!(TimeUnit::from_ordinal(0), TimeUnit::Hours);
        assert_eq!(TimeUnit::from_ordinal(1), TimeUnit::Days);
    }

    #[test]
    fn to_seconds_converts_each_unit() {
        assert_eq!(TimeUnit::Hours.to_seconds(3), 3 * 3600);
        assert_eq!(TimeUnit::Days.to_seconds(2), 2 * 86400);
    }

    #[test]
    fn is_stale_is_false_for_a_freshly_fetched_policy_and_true_once_the_age_is_reached() {
        assert!(!is_stale(&policy_fetched(0), 3600));
        assert!(is_stale(&policy_fetched(3600), 3600));
    }

    #[test]
    fn load_cached_returns_none_when_nothing_has_been_cached_yet() {
        let path = std::env::temp_dir().join(format!("kintsugi-policy-missing-{}.json", std::process::id()));
        let _ = fs::remove_file(&path);

        assert!(load_cached(&path).is_none());
    }

    #[test]
    fn load_cached_round_trips_a_written_policy() {
        let path = std::env::temp_dir().join(format!("kintsugi-policy-roundtrip-{}.json", std::process::id()));
        let policy = policy_fetched(0);
        fs::write(&path, serde_json::to_string(&policy).unwrap()).unwrap();

        let loaded = load_cached(&path).expect("a policy written by the service must be readable by the tray process");

        assert_eq!(loaded.interval_seconds(), policy.interval_seconds());
        assert_eq!(loaded.delay_seconds(), policy.delay_seconds());
        assert_eq!(loaded.max_delay_count, policy.max_delay_count);

        let _ = fs::remove_file(&path);
    }

    #[test]
    fn delay_label_reads_naturally_in_a_button() {
        assert_eq!(policy_fetched(0).delay_label(), "2 hour(s)");
    }
}
