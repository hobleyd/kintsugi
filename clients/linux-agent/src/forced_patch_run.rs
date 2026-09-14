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
/// through the root service, so `upgrade::is_patchable` still verifies the script's signature against
/// the artifact-signing key pinned at enrollment. Forcing a run is an urgency override, never a trust
/// override.
///
/// `Serialize` as well as `Deserialize`, as on Windows and unlike macOS: this crosses the queue
/// between the root service that fetched it and the per-user process that acts on it (see
/// `queue::RequestResult`), because the per-user process here holds no identity and makes no network
/// call at all.
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

/// How often the per-user process asks the root service whether anything has been forced.
///
/// **Five minutes here, where the other two agents ask once a scheduler tick, and the systemd unit
/// is the reason.** On macOS a poll is an HTTP call the per-user process makes itself; on Windows it
/// is a message to a service that is already resident. Here it is neither: the queue is watched by
/// `kintsugi-agent-queue.path`, so every poll *starts a systemd unit* — a fresh
/// `kintsugi-agent --process-queue` process, taking the lock and logging its start and finish to the
/// journal. Once a minute that is 1440 unit activations a day on every Linux desktop, which reads as
/// a broken agent long before anybody reads the lines.
///
/// Very little is lost by the difference: the forced cycle it starts then waits `WARNING_PERIOD`
/// (five minutes) before patching anything, so the poll interval is at most half of a wait that is
/// dominated by the notice either way.
pub const POLL_INTERVAL: Duration = Duration::from_secs(5 * 60);

/// How long the per-user process waits for the root service to answer one poll. Generous next to the
/// work involved — a unit start, plus an HTTP call with its own retry budget — because the
/// alternative is worse than waiting: the service marks the server's rows collected as it reads them,
/// so a caller that gave up early would lose an instruction that had already been consumed.
const POLL_TIMEOUT: Duration = Duration::from_secs(2 * 60);

/// Collects everything an administrator has raised against this host since the last poll — and, by
/// the act of reading it, consumes it. The server stamps each row collected as it answers, so an
/// instruction is handed over exactly once: a row served again on the next poll would put this host
/// into a fresh five-minute patching warning for as long as it lived.
///
/// Called by the **root** side only — the per-user process holds no identity and makes no network
/// call at all (see `clients/CLAUDE.md`), so it asks through the queue instead. Two callers, and they
/// are the two halves of the Linux split: `main::ServiceHandler` answering a
/// `RequestKind::ForcedPatchRuns` for a desktop, and `main::patch_unattended_if_nobody_is_logged_in`
/// for a server with nobody on it.
///
/// Takes the short-budget `get_with_retry` rather than `post_with_retry`: a poll that is holding up
/// an emergency cannot afford minutes of backoff, and the next poll comes round regardless. An error
/// is the caller's to log and otherwise ignore — a server that cannot be reached is not an emergency
/// on this host, and nothing has been consumed.
pub fn collect(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) -> Result<Vec<ForcedPatchRun>> {
    let response = crate::get_with_retry("forced patch runs for this host", || {
        client.get(config.forced_patch_runs_url()).query(&[("serialNumber", serial_number)])
    })?;

    if !response.status().is_success() {
        anyhow::bail!("request rejected (HTTP {})", response.status());
    }

    response.json::<Vec<ForcedPatchRun>>().context("could not parse response")
}

/// The per-user end of one poll: a queue round trip to the root service, which does the asking.
///
/// Shared by the poller thread below and by `main::QueueClient`'s `RequestHandler` implementation,
/// so there is one description of what a forced-run request looks like from this side rather than
/// two that can drift.
pub fn ask_service(queue_dir: &std::path::Path) -> Result<Vec<ForcedPatchRun>> {
    let result = queue::submit(queue_dir, RequestKind::ForcedPatchRuns, "", POLL_TIMEOUT)?;
    if !result.success {
        anyhow::bail!("the root service could not check for forced patch runs: {}", result.output.trim());
    }
    Ok(result.forced)
}

/// The per-user end of the poll, on a thread of its own, sending whatever it collects down the
/// returned channel.
///
/// A thread rather than an inline call in the scheduler loop, for the same reason `spawn_cycle`
/// exists: a queue round trip is a unit start and a result file polled for once a second, and the
/// loop it would block is the one that has to answer a click in the tray.
///
/// Nothing is lost while a patch cycle is in flight: the channel holds what this collects, and the
/// scheduler drains it when the schedule state comes back (see `run_scheduler`'s `pending_forced`).
pub fn spawn_poller(interval: Duration) -> mpsc::Receiver<Vec<ForcedPatchRun>> {
    let (tx, rx) = mpsc::channel();

    std::thread::spawn(move || {
        loop {
            // Sleeps first: on a fresh login the root service may not have run at all yet, and a
            // poll it cannot answer is a timeout this side waits two minutes for.
            std::thread::sleep(interval);

            match ask_service(&config::queue_dir()) {
                Ok(forced) => {
                    if !forced.is_empty() && tx.send(forced).is_err() {
                        // The scheduler is gone, so this process is on its way down.
                        return;
                    }
                }
                Err(err) => logging::warn(&format!("could not ask the root service for forced patch runs: {err:#}")),
            }
        }
    });

    rx
}
