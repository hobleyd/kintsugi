use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use serde::Deserialize;

/// Default backend base URL, used when no config file or environment override is present. Kept in
/// step with the macOS agent's own `DEFAULT_API_BASE_URL`.
const DEFAULT_API_BASE_URL: &str = "https://kintsugi.example.com:8443";

const ENV_OVERRIDE: &str = "PATCHING_AGENT_API_BASE_URL";
const ENROLLMENT_TOKEN_ENV_OVERRIDE: &str = "PATCHING_AGENT_ENROLLMENT_TOKEN";

/// Everything machine-wide this agent owns lives under one directory, the way the macOS agent uses
/// `/Library/Application Support/kintsugi-agent` — so `self_removal` can delete one tree rather
/// than hunting individual files. `%ProgramData%` rather than a hardcoded `C:\ProgramData` because
/// the system drive is not guaranteed to be C:.
fn program_data_root() -> PathBuf {
    let program_data = std::env::var("ProgramData")
        .ok()
        .filter(|v| !v.trim().is_empty())
        .unwrap_or_else(|| r"C:\ProgramData".to_string());
    PathBuf::from(program_data).join(r"Kintsugi\kintsugi-agent")
}

/// Where this agent installs itself — also its own self-update target: `self_update::check_and_apply`
/// replaces exactly this path. `%ProgramFiles%` for the same reason as above.
fn program_files_root() -> PathBuf {
    let program_files = std::env::var("ProgramFiles")
        .ok()
        .filter(|v| !v.trim().is_empty())
        .unwrap_or_else(|| r"C:\Program Files".to_string());
    PathBuf::from(program_files).join("Kintsugi")
}

/// The Windows service name the SYSTEM half of this agent is registered under (see
/// packaging/install.ps1) — the counterpart to the macOS agent's `DAEMON_LAUNCHD_LABEL`. Kept here
/// rather than only in the installer script so `self_update` and `self_removal` can address the
/// service by name once they've replaced or removed the binary behind it.
pub const SERVICE_NAME: &str = "KintsugiAgent";

/// The scheduled task that starts the per-user tray process in every interactive logon session —
/// the counterpart to the macOS agent's `UI_LAUNCHD_LABEL` LaunchAgent. A logon-triggered task with
/// a group principal is Windows' nearest equivalent to `/Library/LaunchAgents`: one machine-wide
/// definition that launches once per user, in that user's own session and at that user's own
/// privilege level.
pub const UI_TASK_NAME: &str = r"\Kintsugi\Kintsugi Agent UI";

#[derive(Debug, Deserialize, Default)]
struct FileConfig {
    api_base_url: Option<String>,
    enrollment_token: Option<String>,
}

#[derive(Debug, Clone)]
pub struct Config {
    pub api_base_url: String,
    /// The one-time shared secret this agent presents to enroll (see `identity::enroll`). Only
    /// needed until enrollment succeeds; safe to remove from config.toml afterward, though leaving
    /// it is harmless since it's never used again once an identity exists on disk.
    pub enrollment_token: Option<String>,
}

impl Config {
    /// Resolves configuration in priority order: environment variable, then config file, then the
    /// built-in default. Each field is resolved independently, so an environment override for one
    /// doesn't suppress the file for the other.
    pub fn load() -> Self {
        Self::load_from(&default_config_path())
    }

    fn load_from(config_path: &Path) -> Self {
        let file_config = std::fs::read_to_string(config_path)
            .ok()
            .and_then(|contents| toml::from_str::<FileConfig>(&contents).ok())
            .unwrap_or_default();

        let api_base_url = std::env::var(ENV_OVERRIDE)
            .ok()
            .filter(|v| !v.trim().is_empty())
            .or(file_config.api_base_url)
            .unwrap_or_else(|| DEFAULT_API_BASE_URL.to_string());

        let enrollment_token = std::env::var(ENROLLMENT_TOKEN_ENV_OVERRIDE)
            .ok()
            .filter(|v| !v.trim().is_empty())
            // The packaged config.toml ships with an empty placeholder (`enrollment_token = ""`)
            // for whoever builds the installer to fill in — filtered here the same as the env
            // override above, so a still-blank placeholder is treated as "not configured" and
            // fails with a clear error, rather than silently POSTing an empty token to the server
            // and getting back a generic, hard-to-diagnose 400.
            .or(file_config.enrollment_token.filter(|v| !v.trim().is_empty()));

        Self { api_base_url, enrollment_token }
    }

    pub fn enroll_url(&self) -> String {
        format!("{}/api/host/enroll", self.api_base_url.trim_end_matches('/'))
    }

    pub fn register_host_url(&self) -> String {
        format!("{}/api/host", self.api_base_url.trim_end_matches('/'))
    }

    /// The remote control socket's address.
    ///
    /// One path for both sockets — the standing control one (no `session_id`) and a session's media
    /// one (with) — because nginx gates this route with an exact match on a single path segment and
    /// tells the two apart by query string. See nginx/default.conf and RemoteControlController.
    ///
    /// The scheme is rewritten because `api_base_url` is an HTTP address and tungstenite will not
    /// accept one: it requires `ws`/`wss` and refuses the request outright rather than assuming.
    pub fn remote_control_url(&self, serial_number: &str, session_id: Option<&str>) -> String {
        let base = self.api_base_url.trim_end_matches('/');
        let base = match base.split_once("://") {
            Some(("https", rest)) => format!("wss://{rest}"),
            Some(("http", rest)) => format!("ws://{rest}"),
            // Already a socket scheme, or something unrecognised — passed through so a
            // misconfiguration fails at connect time with the address in the message, rather than
            // being silently rewritten into a different one.
            _ => base.to_string(),
        };

        match session_id {
            Some(session_id) => format!("{base}/api/remote-control?serialNumber={serial_number}&sessionId={session_id}"),
            None => format!("{base}/api/remote-control?serialNumber={serial_number}"),
        }
    }

    pub fn register_applications_url(&self) -> String {
        format!("{}/api/applications", self.api_base_url.trim_end_matches('/'))
    }

    /// Base URL only — the caller adds `?serialNumber=` via the HTTP client's own query-building
    /// (`RequestBuilder::query`) rather than manual string formatting, so the serial number gets
    /// properly percent-encoded.
    pub fn upgrade_paths_url(&self) -> String {
        format!("{}/api/upgrade-paths", self.api_base_url.trim_end_matches('/'))
    }

    /// The fleet-wide patching schedule (how often, delay length, max delays) — see
    /// Kintsugi.WebApi/Controllers/PatchingPolicyController.cs.
    pub fn patching_policy_url(&self) -> String {
        format!("{}/api/patching-policy", self.api_base_url.trim_end_matches('/'))
    }

    /// The latest published kintsugi-agent build for one platform — see
    /// Kintsugi.WebApi/Controllers/AgentPackagesController.cs and `self_update`.
    pub fn agent_package_latest_url(&self, platform: &str) -> String {
        format!("{}/api/agent-packages/{platform}/latest", self.api_base_url.trim_end_matches('/'))
    }

    /// Downloads the latest published package file for one platform — see `self_update`.
    pub fn agent_package_download_url(&self, platform: &str) -> String {
        format!("{}/api/agent-packages/{platform}/download", self.api_base_url.trim_end_matches('/'))
    }

    /// Tells the server an application was just successfully patched — see
    /// `upgrade::report_patch_result` and Kintsugi.WebApi/Controllers/ApplicationsController.cs.
    pub fn patch_result_url(&self) -> String {
        format!("{}/api/patch-results", self.api_base_url.trim_end_matches('/'))
    }

    /// Tells the server a pending Windows update was just successfully installed — see
    /// `os_update::report_patched` and Kintsugi.WebApi/Controllers/HostsController.cs.
    pub fn os_patch_result_url(&self) -> String {
        format!("{}/api/os-patch-results", self.api_base_url.trim_end_matches('/'))
    }

    /// Tells the server this host finished uninstalling itself completely, after a check-in
    /// response marked it for removal — see `self_removal::run` and
    /// Kintsugi.WebApi/Controllers/HostsController.cs.
    pub fn host_removed_url(&self) -> String {
        format!("{}/api/host-removed", self.api_base_url.trim_end_matches('/'))
    }
}

/// The parent of every machine-wide path this agent owns (config, identity, queue, service log,
/// check-in schedule, the shared policy cache) — a full removal (`self_removal`) deletes this one
/// directory rather than each file individually.
pub fn config_dir() -> PathBuf {
    program_data_root()
}

pub fn default_config_path() -> PathBuf {
    program_data_root().join("config.toml")
}

/// Where this agent's mutual-TLS identity (certificate, private key, pinned CA and
/// artifact-signing public key — see `identity`) is persisted once enrolled.
///
/// Unlike the macOS agent — where the per-user process reads this same identity and the directory
/// therefore has to be group-readable — nothing but the SYSTEM service ever opens this on Windows.
/// The tray process gets what it needs through the queue instead (see `queue`), so this directory
/// is locked to SYSTEM and Administrators and the client private key is never readable by whoever
/// happens to be logged in. See `identity::restrict_identity_permissions`.
pub fn identity_dir() -> PathBuf {
    program_data_root().join("identity")
}

/// Shared handoff directory between the SYSTEM service and the per-user tray process, for
/// everything the tray process is deliberately not privileged enough to do itself: installing
/// Windows updates, running an application's upgrade script, and even asking what's pending (which
/// needs the mutual-TLS identity above). Writable by `BUILTIN\Users` so a logged-in user can drop a
/// request, and acted on only by the service — see `queue` and packaging/install.ps1.
pub fn queue_dir() -> PathBuf {
    program_data_root().join("queue")
}

/// The service's own durable action log — a guaranteed location regardless of how the process was
/// invoked. See `logging`.
pub fn service_log_path() -> PathBuf {
    program_data_root().join("service.log")
}

/// This host's assigned check-in minute-of-hour (0-59), persisted so it survives across service
/// restarts — see `checkin_schedule`. Deliberately separate from the config file: that is always
/// overwritten wholesale on every install/reinstall (see packaging/install.ps1), whereas this needs
/// to survive one.
pub fn checkin_schedule_path() -> PathBuf {
    program_data_root().join("checkin-schedule.json")
}

/// Where the service writes the fleet-wide patching policy it fetched, for the tray process to
/// read. Machine-wide and world-readable rather than per-user, because the tray process has no
/// identity of its own to fetch it with (see `identity_dir`) — and the policy is a schedule, not a
/// secret. The macOS agent's per-user process fetches this itself; here the service is the only
/// thing that talks to the server at all.
pub fn policy_cache_path() -> PathBuf {
    program_data_root().join("policy.json")
}

pub fn installed_binary_path() -> PathBuf {
    program_files_root().join("kintsugi-agent.exe")
}

/// Where the per-user tray process (`--agent`) keeps its own state: the scheduling state (next due
/// time, delays used) and its log. Under the invoking user's own profile since, unlike the
/// service's config, this process never runs with elevated rights.
pub fn user_state_dir() -> Result<PathBuf> {
    let local_app_data = std::env::var("LOCALAPPDATA")
        .ok()
        .filter(|v| !v.trim().is_empty())
        .map(PathBuf::from)
        // A scheduled task started in a fresh logon session is not guaranteed the same environment
        // an interactive shell gets — the macOS agent hit exactly this with a missing `$HOME` (see
        // its own config::user_state_dir) — so fall back to composing the path from USERPROFILE
        // before giving up.
        .or_else(|| {
            std::env::var("USERPROFILE")
                .ok()
                .filter(|v| !v.trim().is_empty())
                .map(|profile| PathBuf::from(profile).join(r"AppData\Local"))
        })
        .context("could not determine the current user's local application data directory (checked %LOCALAPPDATA% and %USERPROFILE%)")?;

    Ok(local_app_data.join(r"Kintsugi\kintsugi-agent"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn urls_are_built_without_a_double_slash_when_the_base_has_a_trailing_one() {
        let config = Config { api_base_url: "https://example.test:8443/".to_string(), enrollment_token: None };

        assert_eq!(config.register_host_url(), "https://example.test:8443/api/host");
        assert_eq!(config.enroll_url(), "https://example.test:8443/api/host/enroll");
        assert_eq!(
            config.agent_package_latest_url("windows"),
            "https://example.test:8443/api/agent-packages/windows/latest"
        );
    }

    #[test]
    fn load_from_a_missing_file_falls_back_to_the_built_in_default() {
        let config = Config::load_from(Path::new(r"C:\this\does\not\exist\config.toml"));

        assert_eq!(config.api_base_url, DEFAULT_API_BASE_URL);
        assert!(config.enrollment_token.is_none());
    }

    #[test]
    fn every_machine_wide_path_lives_under_the_one_directory_self_removal_deletes() {
        // self_removal deletes config_dir() as a single tree — anything that escaped it would
        // survive a "complete" uninstall the server asked for.
        let root = config_dir();

        for path in [default_config_path(), identity_dir(), queue_dir(), service_log_path(), checkin_schedule_path(), policy_cache_path()] {
            assert!(path.starts_with(&root), "{} is not under {}", path.display(), root.display());
        }
    }
}
