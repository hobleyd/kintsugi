use std::sync::mpsc;
use std::time::Duration;

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

use crate::config::{self, Config};
use crate::logging;
use crate::queue::{self, RequestKind};

/// One "patch this application now" instruction raised by an administrator from the admin UI's
/// Installed Applications screen, against the hosts that screen was filtered to.
///
/// Mirrors `ForcedPatchRunDto` — see Kintsugi.Application/ForcedPatchRuns/ForcedPatchRunDtos.cs, and
/// the remarks on `Kintsugi.Domain.Entities.ForcedPatchRun` for why the row carries a name and
/// nothing else. **No script travels with it**: the cycle it starts re-fetches this host's work list
/// through the service, so `upgrade::is_patchable` still verifies the script's signature against the
/// artifact-signing key pinned at enrollment. Forcing a run is an urgency override, never a trust
/// override.
///
/// `Serialize` as well as `Deserialize`, unlike the other two agents': on Windows this crosses the
/// queue between the service that fetched it and the tray process that acts on it (see
/// `queue::RequestResult`), which the other two do not need because their per-user process holds a
/// mutual-TLS identity of its own and asks the server directly.
#[derive(Debug, Clone, Serialize, Deserialize)]
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
/// Called by the *service*, not by the tray process, and that is the Windows-specific half of this:
/// the tray holds no mutual-TLS identity (see `config::identity_dir`), so it cannot ask the server
/// anything directly. It asks through the queue instead, exactly as it does for a plan — see
/// `queue::RequestKind::ForcedPatchRuns`.
///
/// Takes the short-budget `get_with_retry` rather than `post_with_retry`: a poll that is holding up
/// an emergency cannot afford minutes of backoff, and the next tick is a minute away regardless. An
/// error is the caller's to log and otherwise ignore — a server that cannot be reached is not an
/// emergency on this host, and nothing has been consumed.
pub fn collect(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) -> Result<Vec<ForcedPatchRun>> {
    let response = crate::service::get_with_retry("forced patch runs for this host", || {
        client.get(config.forced_patch_runs_url()).query(&[("serialNumber", serial_number)])
    })?;

    if !response.status().is_success() {
        anyhow::bail!("request rejected (HTTP {})", response.status());
    }

    response.json::<Vec<ForcedPatchRun>>().context("could not parse response")
}

/// How long the tray waits for the service to answer one poll. Generous next to the work involved —
/// the service polls the queue every `service::QUEUE_POLL_INTERVAL` and then makes an HTTP call with
/// its own retry budget — because the alternative is worse than waiting: the service marks the
/// server's rows collected as it reads them, so a tray that gave up early would lose an instruction
/// that had already been consumed.
const POLL_TIMEOUT: Duration = Duration::from_secs(2 * 60);

/// The tray's end of the poll, on a thread of its own, sending whatever it collects down the
/// returned channel.
///
/// **A thread here, where the other two agents poll inline in their scheduler loop, and the
/// difference is the queue.** Those two make one HTTP call from the scheduler thread, beside the
/// policy fetch that is already there. This agent cannot: the tray holds no mutual-TLS identity, so
/// a poll is a file written into the queue, a service that notices it within
/// `service::QUEUE_POLL_INTERVAL`, and a result file this side polls for once a second. Seconds,
/// every minute, on the thread that also has to answer a click in the notification area — which is
/// the exact problem `spawn_cycle` exists to keep out of that loop.
///
/// Nothing is lost while a patch cycle is in flight: the channel holds what this collects, and the
/// scheduler drains it when the state comes back (see `run_scheduler`'s `pending_forced`).
pub fn spawn_poller(interval: Duration) -> mpsc::Receiver<Vec<ForcedPatchRun>> {
    let (tx, rx) = mpsc::channel();

    std::thread::spawn(move || {
        loop {
            // Sleeps first: the service has only just started at this point on a fresh boot, and a
            // poll it cannot answer is a timeout this side waits two minutes for.
            std::thread::sleep(interval);

            match queue::submit(&config::queue_dir(), RequestKind::ForcedPatchRuns, "", POLL_TIMEOUT) {
                Ok(result) if result.success => {
                    if !result.forced.is_empty() && tx.send(result.forced).is_err() {
                        // The scheduler is gone, so this process is on its way down.
                        return;
                    }
                }
                Ok(result) => logging::warn(&format!("the agent service could not check for forced patch runs: {}", result.output)),
                Err(err) => logging::warn(&format!("could not ask the agent service for forced patch runs: {err:#}")),
            }
        }
    });

    rx
}
