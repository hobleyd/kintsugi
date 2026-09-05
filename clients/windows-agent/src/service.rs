use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

use crate::config::{self, Config};
use crate::identity::{self, AgentIdentity};
use crate::logging;
use crate::os_update;
use crate::policy;
use crate::queue::{self, Plan, PlannedApp, RequestHandler};
use crate::self_removal;
use crate::self_update;
use crate::system_info::{self, InstalledApp};
use crate::upgrade;
use crate::{checkin_schedule, MAX_ATTEMPTS, INITIAL_BACKOFF};

/// How often the service wakes to look at the queue when no check-in is due.
///
/// The macOS agent's daemon has no loop at all — launchd re-invokes it, and a `WatchPaths` entry on
/// the queue directory wakes it on demand. A Windows service is resident instead, so it owns both:
/// this is the queue's polling interval, kept short because a user is sitting in front of a progress
/// window waiting for the request to be answered.
const QUEUE_POLL_INTERVAL: Duration = Duration::from_secs(2);

/// How long a cached patching policy is trusted before the service bothers re-fetching it — the
/// policy changes rarely, so there's no need to hit the server on every check-in.
const POLICY_REFRESH_INTERVAL: u64 = 60 * 60;

#[derive(Debug, Serialize)]
struct RegisterHostRequest {
    hostname: String,
    #[serde(rename = "serialNumber")]
    serial_number: String,
    /// The minute-of-hour (0-59) this host currently checks in on — see `checkin_schedule`. Sent on
    /// every check-in so the server can track load per minute and, in its response, tell this host
    /// to move to a different one if its current minute is carrying more than its share.
    #[serde(rename = "checkInMinute")]
    check_in_minute: u8,
    #[serde(rename = "operatingSystem", skip_serializing_if = "Option::is_none")]
    operating_system: Option<String>,
    #[serde(rename = "ipAddress", skip_serializing_if = "Option::is_none")]
    ip_address: Option<String>,
    #[serde(rename = "operatingSystemUpdateAvailable", skip_serializing_if = "Option::is_none")]
    operating_system_update_available: Option<bool>,
    #[serde(rename = "operatingSystemLatestVersion", skip_serializing_if = "Option::is_none")]
    operating_system_latest_version: Option<String>,
    /// This build's own version, so the Hosts screen can show which agent release each host is
    /// running — mirrors `CreateHostCommand.AgentVersion`. Always sent: the agent always knows it.
    #[serde(rename = "agentVersion")]
    agent_version: &'static str,
}

/// Mirrors the backend's `CreateHostResult` — see
/// Kintsugi.Application/Hosts/Commands/CreateHost/CreateHostCommand.cs. Fields this agent has no use
/// for (host, wasCreated) stay omitted, the same way `self_update`'s `AgentPackageInfo` omits ones
/// it doesn't need.
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RegisterHostResponse {
    #[serde(default)]
    suggested_check_in_minute: Option<u8>,
    /// Set once an admin has requested this host be removed (see
    /// Kintsugi.Domain.Entities.Host.RemovalRequested) — tells this check-in to uninstall the agent
    /// completely instead of continuing on to application registration and everything else below
    /// it. See `self_removal::run`.
    #[serde(default)]
    removal_requested: bool,
}

#[derive(Debug, Serialize)]
struct RegisterApplicationsRequest {
    #[serde(rename = "serialNumber")]
    serial_number: String,
    applications: Vec<InstalledApp>,
}

/// Everything the service needs to answer a queue request or run a check-in, established once at
/// startup and reused for the life of the process.
pub struct Agent {
    config: Config,
    client: reqwest::blocking::Client,
    identity: Option<AgentIdentity>,
    serial_number: String,
    /// This host's assigned check-in minute and where it is persisted — owned here rather than by
    /// `run_loop` because a check-in can now also be asked for over the queue ("Check In Now"), and
    /// both callers have to apply the server's answer the same way. See `check_in`.
    schedule_path: PathBuf,
    check_in_minute: u8,
    /// Set once a check-in response has asked for removal, so the loop stops rather than carrying on
    /// against files it has just deleted.
    removed: bool,
}

impl Agent {
    pub fn new() -> Result<Self> {
        let config = Config::load();
        let serial_number = system_info::serial_number().context("could not determine this machine's identifier")?;

        let schedule_path = config::checkin_schedule_path();
        let check_in_minute = checkin_schedule::load_or_assign(&schedule_path);

        // Enrolls on first run; reuses the same identity from then on, until it needs replacing
        // (e.g. this host was decommissioned and re-provisioned). Every agent-only route is
        // rejected by nginx without the resulting certificate — see nginx/default.conf.
        let identity = identity::load_or_enroll(&config, &serial_number);
        let client = identity::build_client(Duration::from_secs(15), identity.as_ref()).context("failed to build HTTP client")?;

        Ok(Self { config, client, identity, serial_number, schedule_path, check_in_minute, removed: false })
    }

    pub fn serial_number(&self) -> &str {
        &self.serial_number
    }

    pub fn check_in_minute(&self) -> u8 {
        self.check_in_minute
    }

    /// Re-checks disk for an identity this service failed to enroll earlier.
    ///
    /// Enrollment can fail for entirely transient reasons — the machine booted before the network
    /// was up, or the configured token had just been rotated — and without this the service would
    /// stay unenrolled (and so unable to do anything at all) until someone restarted it.
    fn ensure_identity(&mut self) {
        if self.identity.is_some() {
            return;
        }

        self.identity = identity::load_or_enroll(&self.config, &self.serial_number);
        if self.identity.is_some() {
            match identity::build_client(Duration::from_secs(15), self.identity.as_ref()) {
                Ok(client) => {
                    self.client = client;
                    logging::info("agent identity established; resuming normal operation");
                }
                Err(err) => {
                    logging::error(&format!("enrolled an identity but could not build a client with it: {err:#}"));
                    self.identity = None;
                }
            }
        }
    }

    /// One full check-in: register this host and its installed applications, then check for a newer
    /// build of this agent itself, then adopt whichever check-in minute the server wants this host
    /// to use from now on.
    ///
    /// The same sequence the macOS daemon runs per invocation, minus the queue drain — a resident
    /// service handles the queue continuously in its own loop rather than once per check-in. Run on
    /// the hour by `run_loop`, once by `--check-in`, and on demand by a `RequestKind::CheckIn`.
    pub fn check_in(&mut self) -> Result<()> {
        self.ensure_identity();

        let hostname = system_info::hostname().context("could not determine hostname")?;

        // Best-effort: registration still proceeds with just hostname + serial number if either of
        // these can't be determined.
        let operating_system = system_info::operating_system()
            .inspect_err(|err| logging::warn(&format!("could not determine operating system: {err}")))
            .ok();
        let ip_address = system_info::local_ip_address()
            .inspect_err(|err| logging::warn(&format!("could not determine local IP address: {err}")))
            .ok();

        // Best-effort, same as the OS name/version above: a host that can't run the standard update
        // check for some reason still gets registered, just without this piece reported.
        let os_update_status = os_update::check()
            .inspect_err(|err| logging::warn(&format!("could not check for Windows updates: {err}")))
            .ok();

        logging::info(&format!(
            "registering host: hostname={hostname} serial_number={} operating_system={operating_system:?} ip_address={ip_address:?} os_update_status={os_update_status:?}",
            self.serial_number
        ));

        let host_request = RegisterHostRequest {
            hostname,
            serial_number: self.serial_number.clone(),
            check_in_minute: self.check_in_minute,
            operating_system,
            ip_address,
            operating_system_update_available: os_update_status.as_ref().map(|s| s.available),
            operating_system_latest_version: os_update_status.and_then(|s| s.latest_version),
            agent_version: env!("CARGO_PKG_VERSION"),
        };
        let host_response: RegisterHostResponse =
            post_with_retry(&self.client, &self.config.register_host_url(), &host_request).context("failed to register host")?;

        if host_response.removal_requested {
            logging::info("the server has marked this host for removal — uninstalling instead of continuing this check-in");
            self_removal::run(&self.client, &self.config, &self.serial_number);
            self.removed = true;
            return Ok(());
        }

        let applications = collect_installed_applications();
        logging::info(&format!("reporting {} installed application(s)", applications.len()));

        let applications_request = RegisterApplicationsRequest {
            serial_number: self.serial_number.clone(),
            applications,
        };
        let _: serde_json::Value = post_with_retry(&self.client, &self.config.register_applications_url(), &applications_request)
            .context("failed to register installed applications")?;

        // Refreshed here rather than by the tray process, which has no identity of its own to fetch
        // it with — the cache file this writes is how the policy reaches it at all. See `queue`.
        let cache_path = config::policy_cache_path();
        let should_refresh = policy::load_cached(&cache_path).is_none_or(|cached| policy::is_stale(&cached, POLICY_REFRESH_INTERVAL));
        if should_refresh {
            policy::refresh(&self.client, &self.config, &cache_path);
        }

        // Last, and only after everything above has already succeeded: check whether a newer build
        // of this agent itself has been published, and install it in place if so — see
        // `self_update`. There's no policy/schedule governing the agent's own updates.
        self_update::check_and_apply(&self.client, &self.config, self.identity.as_ref(), env!("CARGO_PKG_VERSION"));

        // Applied last, matching the macOS ordering, so a minute change never lands halfway through
        // a check-in.
        let target_minute = host_response.suggested_check_in_minute.unwrap_or(self.check_in_minute);
        self.check_in_minute = checkin_schedule::apply(&self.schedule_path, self.check_in_minute, target_minute);

        Ok(())
    }

    pub fn was_removed(&self) -> bool {
        self.removed
    }

    /// This host's upgrade statuses, filtered to what is actually runnable — the same
    /// `is_patchable` gate the macOS agent applies, run here rather than in the UI process because
    /// verifying a signature needs the pinned artifact-signing key, which lives with the identity.
    fn patchable(&self) -> Result<Vec<upgrade::UpgradeStatus>> {
        let identity = self.identity.as_ref().context("this agent has not enrolled an identity yet")?;
        let statuses = upgrade::fetch_upgrade_statuses(&self.client, &self.config, &self.serial_number)?;
        Ok(statuses.into_iter().filter(|status| upgrade::is_patchable(status, identity)).collect())
    }
}

impl RequestHandler for Agent {
    fn plan(&mut self) -> Result<Plan> {
        self.ensure_identity();

        let apps = self
            .patchable()?
            .into_iter()
            .map(|status| PlannedApp {
                application_name: status.application_name,
                latest_version: status.latest_version,
            })
            .collect();

        let os_update_available = os_update::check().map(|status| status.available).unwrap_or_else(|err| {
            logging::warn(&format!("could not check for Windows updates: {err:#}"));
            false
        });

        Ok(Plan { apps, os_update_available })
    }

    /// Runs one application's upgrade.
    ///
    /// The work list is re-fetched from the server here rather than trusted from the request, which
    /// is the property that makes the queue safe: the request named an application, and everything
    /// actually executed — the script, its signature, the identifier it's addressed by — comes from
    /// the server and is verified against the pinned artifact-signing key. A request that names an
    /// application with no signed, patchable upgrade path simply fails. See `queue`.
    fn patch_application(&mut self, application_name: &str) -> Result<()> {
        self.ensure_identity();
        let identity = self.identity.as_ref().context("this agent has not enrolled an identity yet")?;

        let status = self
            .patchable()?
            .into_iter()
            .find(|status| status.application_name.eq_ignore_ascii_case(application_name))
            .with_context(|| format!("'{application_name}' has no signed, patchable upgrade path"))?;

        logging::info(&format!("attempting to patch {} (method {:?})", status.application_name, status.method));
        upgrade::patch_one(&status, identity)?;
        logging::info(&format!("patched {} successfully", status.application_name));

        match &status.latest_version {
            Some(new_version) => upgrade::report_patch_result(
                &self.client,
                &self.config,
                &self.serial_number,
                &status.application_name,
                new_version,
            ),
            None => logging::warn(&format!(
                "patched {} successfully, but no latest_version was known to report to the server",
                status.application_name
            )),
        }

        Ok(())
    }

    fn install_os_updates(&mut self) -> Result<()> {
        os_update::install()?;
        // Reported from here, not from the tray process, for the same reason the patch result above
        // is: this is the side holding the identity every agent-only route requires.
        os_update::report_patched(&self.client, &self.config, &self.serial_number);
        Ok(())
    }

    /// Answers a [`queue::RequestKind::CheckIn`]: the same check-in `run_loop` runs on the hour,
    /// brought forward. `run_loop` leaves its own next wake-up where it was — an extra check-in at
    /// the hour costs two requests and nothing else, and the tray's "Next check-in" line predicts
    /// that hourly firing rather than this one.
    fn check_in(&mut self) -> Result<String> {
        Agent::check_in(self)?;
        Ok(format!("checked in as {} (agent {})", self.serial_number, env!("CARGO_PKG_VERSION")))
    }
}

/// Combines the uninstall-registry scan with the package managers'. A winget- or Chocolatey-managed
/// application also has an uninstall-registry entry, so the registry scan is told which keys the
/// managers already account for (`managed_keys`) and skips those, keeping the manager-tagged entry
/// as the single source of truth for that application rather than also reporting it as a separate,
/// unmanaged one. Any remaining exact duplicates are still
/// deduplicated, since the backend rejects duplicate (host, name, version) rows in a single report.
///
/// Structurally identical to the macOS agent's `collect_installed_applications`, with winget and
/// Chocolatey in Homebrew's place.
pub fn collect_installed_applications() -> Vec<InstalledApp> {
    use std::collections::HashSet;

    let managers = system_info::scan_package_managers();

    let mut seen = HashSet::new();
    system_info::scan_installed_programs(&managers.managed_keys)
        .into_iter()
        .chain(managers.apps)
        .filter(|app| seen.insert(app.clone()))
        .collect()
}

pub fn post_with_retry<T: Serialize, R: serde::de::DeserializeOwned>(client: &reqwest::blocking::Client, url: &str, body: &T) -> Result<R> {
    let mut backoff = INITIAL_BACKOFF;
    let mut last_error = None;

    for attempt in 1..=MAX_ATTEMPTS {
        match client.post(url).json(body).send() {
            Ok(response) if response.status().is_success() => {
                let status = response.status();
                let body = response.text().unwrap_or_default();
                logging::info(&format!("POST {url} succeeded (HTTP {status}): {body}"));
                return serde_json::from_str(&body).context("could not parse response body");
            }
            Ok(response) => {
                let status = response.status();
                let body = response.text().unwrap_or_default();
                // A 4xx is not going to fix itself on retry (bad payload, validation failure); fail
                // fast instead of burning the retry budget.
                anyhow::bail!("request rejected (HTTP {status}): {body}");
            }
            Err(err) => {
                // {err:#}, not {err}: anyhow's plain Display prints only the outermost
                // message, and reqwest's outermost message for any connection failure is the
                // bare "error sending request for url (...)" — identical whether the host is
                // unreachable, the TLS handshake was rejected, or DNS failed. The cause chain is
                // where "invalid peer certificate: UnknownIssuer" lives, and without it a server
                // presenting an untrusted certificate is indistinguishable in this log from a
                // network outage. That cost real time to diagnose once; don't drop the `#`.
                logging::warn(&format!("attempt {attempt}/{MAX_ATTEMPTS} to {url} failed: {err:#}"));
                last_error = Some(err);
            }
        }

        if attempt < MAX_ATTEMPTS {
            std::thread::sleep(backoff);
            backoff *= 2;
        }
    }

    Err(anyhow::anyhow!(
        "failed after {MAX_ATTEMPTS} attempts: {}",
        last_error.map(|e| e.to_string()).unwrap_or_default()
    ))
}

fn now_epoch() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs()
}

/// The service's whole working life: check in immediately, then alternate between serving the queue
/// and checking in once an hour at this host's own assigned minute.
///
/// `shutdown` is set by the Service Control Manager handler (see `windows_service`); every wait in
/// here is short enough that a stop request is honoured promptly rather than after an hour.
pub fn run_loop(shutdown: Arc<AtomicBool>) {
    logging::init(&config::service_log_path());

    // Anything left from before a restart or a reboot has an owner that is long gone; acting on it
    // would start an unannounced patch cycle with nothing showing progress.
    queue::discard_stale(&config::queue_dir());
    // The first moment nothing can still be running the copy a previous self-update displaced.
    self_update::clean_up_displaced_binary();

    let mut agent = match Agent::new() {
        Ok(agent) => agent,
        Err(err) => {
            // Nothing this service does is possible without an identifier for the host, and no
            // amount of retrying changes that — see `system_info::serial_number` for why this
            // refuses to invent one.
            logging::error(&format!("could not start the kintsugi-agent service: {err:#}"));
            return;
        }
    };

    logging::info(&format!(
        "kintsugi-agent service starting; api_base_url={} check_in_minute=:{:02}",
        Config::load().api_base_url,
        agent.check_in_minute()
    ));

    // Remote control gets a thread of its own, and it is the only standing outbound connection this
    // agent has. Everything else here is a request the service makes when it has something to say;
    // remote control is the one case where the *server* needs to reach the host, and an hourly
    // check-in cannot carry "somebody would like to see your screen now".
    //
    // Separate from this loop because it spends its life blocked on a pipe and then on sockets,
    // whereas this loop spends its life asleep on a two-second timer. Sharing one would mean a
    // session request waiting for the next tick, and a frame stream inside the queue's poll.
    {
        let remote_config = Config::load();
        let remote_serial = agent.serial_number().to_string();
        let remote_shutdown = shutdown.clone();
        std::thread::spawn(move || crate::remote_control::run(remote_config, remote_serial, remote_shutdown));
    }

    // Runs immediately at startup — the counterpart to the macOS LaunchDaemon's RunAtLoad, and what
    // makes a freshly installed agent appear in the fleet within seconds rather than within an hour.
    let mut next_check_in_at = now_epoch();

    while !shutdown.load(Ordering::SeqCst) {
        if now_epoch() >= next_check_in_at {
            if let Err(err) = agent.check_in() {
                logging::warn(&format!("check-in failed, will retry at the next scheduled time: {err:#}"));
            }

            next_check_in_at = now_epoch() + checkin_schedule::seconds_until(now_epoch(), agent.check_in_minute());
        }

        queue::process_queue(&config::queue_dir(), &mut agent);

        // After the queue rather than only after the hourly check-in: a queue-triggered check-in
        // (see `RequestHandler::check_in`) can learn of a removal too. A drain against the directory
        // `self_removal` has just deleted is a no-op, so nothing is lost by checking once, here.
        if agent.was_removed() {
            logging::info("this host has been removed; the service is stopping");
            return;
        }

        std::thread::sleep(QUEUE_POLL_INTERVAL);
    }

    logging::info("kintsugi-agent service stopping");
}
