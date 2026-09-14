use anyhow::{Context, Result};
use serde::Deserialize;

use crate::config::Config;

/// One "patch this application now" instruction raised by an administrator from the admin UI's
/// Installed Applications screen, against the hosts that screen was filtered to.
///
/// Mirrors `ForcedPatchRunDto` — see Kintsugi.Application/ForcedPatchRuns/ForcedPatchRunDtos.cs, and
/// the remarks on `Kintsugi.Domain.Entities.ForcedPatchRun` for why the row carries a name and
/// nothing else. **No script travels with it**: the cycle it starts fetches this host's work list
/// through `upgrade::fetch_upgrade_statuses` exactly as a scheduled one does, so
/// `upgrade::is_patchable` still verifies the script's signature against the artifact-signing key
/// pinned at enrollment. Forcing a run is an urgency override, never a trust override.
///
/// Kept identical in the other two agents.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ForcedPatchRun {
    /// The server's row id. Never sent back — the server marks the row collected as it answers this
    /// request, which is the whole reason that route is a GET that writes — but carried so a log
    /// line can be matched against the row an administrator raised.
    pub id: String,

    /// The application to patch, named as this host's inventory reports it. Matched
    /// case-insensitively against the work list, the way applications are matched everywhere else
    /// in this agent.
    pub application_name: String,

    /// The upgrade-path platform bucket the forced script is stored under, for the log line only.
    /// This agent resolves its own bucket for this host the same way a scheduled cycle does, so a
    /// value here that disagrees cannot make the wrong script run.
    #[allow(dead_code)]
    pub platform: String,
}

/// Collects everything an administrator has raised against this host since the last poll — and, by
/// the act of reading it, consumes it. The server stamps each row collected as it answers, so an
/// instruction is handed over exactly once: a row served again on the next poll sixty seconds later
/// would put this host into a fresh five-minute patching warning once a minute for as long as it
/// lived.
///
/// Cheap and frequent by design — this is the whole latency budget of an emergency patch, so it
/// rides the scheduler's own 60s tick rather than the hourly policy refresh — which is why it takes
/// the short-budget `get_with_retry` rather than `post_with_retry`: a poll that is holding up an
/// emergency cannot afford minutes of backoff, and the next tick is a minute away regardless.
///
/// An error is the caller's to log and otherwise ignore. A server that cannot be reached is not an
/// emergency on this host; the next tick asks again, and nothing has been consumed.
pub fn collect(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) -> Result<Vec<ForcedPatchRun>> {
    let response = crate::get_with_retry("forced patch runs for this host", || {
        client.get(config.forced_patch_runs_url()).query(&[("serialNumber", serial_number)])
    })?;

    if !response.status().is_success() {
        anyhow::bail!("request rejected (HTTP {})", response.status());
    }

    response.json::<Vec<ForcedPatchRun>>().context("could not parse response")
}
