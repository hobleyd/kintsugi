use std::ffi::CStr;
use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use serde::Deserialize;

/// Default backend base URL, used when no config file or environment
/// override is present. This will change once the backend has a stable,
/// non-development address.
const DEFAULT_API_BASE_URL: &str = "https://kintsugi.example.com:8443";

const CONFIG_PATH: &str = "/Library/Application Support/kintsugi-agent/config.toml";
const ENV_OVERRIDE: &str = "PATCHING_AGENT_API_BASE_URL";
const ENROLLMENT_TOKEN_ENV_OVERRIDE: &str = "PATCHING_AGENT_ENROLLMENT_TOKEN";

/// Where this agent's mutual-TLS identity (certificate, private key, pinned CA and
/// artifact-signing public key — see `identity`) is persisted once enrolled. Shared between the
/// root LaunchDaemon (which performs enrollment) and the per-user `--agent` process (which only
/// ever reads it) — see `identity::restrict_key_permissions` and packaging/install.sh.
const IDENTITY_DIR: &str = "/Library/Application Support/kintsugi-agent/identity";

/// Shared handoff directory between the root LaunchDaemon and the per-user LaunchAgent
/// (`--agent` mode) for the one privileged operation the UI agent can't do itself: installing
/// macOS software updates. Owned `root:admin 0770` by the installer, so only an admin console
/// user can request an install, and only root ever executes one.
const QUEUE_DIR: &str = "/Library/Application Support/kintsugi-agent/queue";

/// The root daemon's own durable action log — a guaranteed location regardless of how the
/// process was invoked (launchd's `StandardOutPath` redirect on top of this is redundant but
/// harmless). See `logging`.
const DAEMON_LOG_PATH: &str = "/Library/Application Support/kintsugi-agent/daemon.log";

/// This host's assigned check-in minute-of-hour (0-59), persisted so it survives across
/// invocations — see `checkin_schedule`. Deliberately separate from `CONFIG_PATH`: that file is
/// always overwritten wholesale on every install/reinstall (see packaging/install.sh), whereas
/// this needs to survive one.
const CHECKIN_SCHEDULE_PATH: &str = "/Library/Application Support/kintsugi-agent/checkin-schedule.json";

/// Where the root LaunchDaemon's own job definition lives — `checkin_schedule` rewrites this file
/// in place (and reloads it with launchd) whenever this host's assigned check-in minute changes,
/// the same way `self_update` replaces `INSTALLED_BINARY_PATH` in place.
const DAEMON_PLIST_PATH: &str = "/Library/LaunchDaemons/au.com.sharpblue.kintsugiagent.plist";

/// Where the per-user LaunchAgent's job definition lives (see packaging/install.sh) — the
/// counterpart to `DAEMON_PLIST_PATH`, torn down alongside it by `self_removal` when a removal is
/// confirmed.
const UI_PLIST_PATH: &str = "/Library/LaunchAgents/au.com.sharpblue.kintsugiagent-ui.plist";

/// Where the root daemon installs itself (see packaging/install.sh) — also this agent's own
/// self-update target: `self_update::check_and_apply` replaces exactly this path.
const INSTALLED_BINARY_PATH: &str = "/usr/local/bin/kintsugi-agent";

/// launchd labels for the two jobs packaging/install.sh installs — kept here (rather than only as
/// bash variables in that script) so `self_update` can `launchctl kickstart` both of them by name
/// once it's replaced the binary they both run.
pub const DAEMON_LAUNCHD_LABEL: &str = "au.com.sharpblue.kintsugiagent";
pub const UI_LAUNCHD_LABEL: &str = "au.com.sharpblue.kintsugiagent-ui";

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
    /// Resolves configuration in priority order: environment variable,
    /// then config file, then the built-in default. Each field is resolved
    /// independently, so an environment override for one doesn't suppress
    /// the file for the other.
    pub fn load() -> Self {
        Self::load_from(Path::new(CONFIG_PATH))
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

    /// Tells the server a pending macOS update was just successfully installed — see
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

pub fn default_config_path() -> PathBuf {
    PathBuf::from(CONFIG_PATH)
}

/// The root daemon / UI agent handoff directory for privileged OS-update installs — see
/// `os_update`. A plain constant rather than something `Config` resolves, since (unlike
/// `api_base_url`) it isn't meant to be end-user-configurable.
pub fn queue_dir() -> PathBuf {
    PathBuf::from(QUEUE_DIR)
}

pub fn identity_dir() -> PathBuf {
    PathBuf::from(IDENTITY_DIR)
}

pub fn daemon_log_path() -> PathBuf {
    PathBuf::from(DAEMON_LOG_PATH)
}

pub fn checkin_schedule_path() -> PathBuf {
    PathBuf::from(CHECKIN_SCHEDULE_PATH)
}

pub fn daemon_plist_path() -> PathBuf {
    PathBuf::from(DAEMON_PLIST_PATH)
}

pub fn ui_plist_path() -> PathBuf {
    PathBuf::from(UI_PLIST_PATH)
}

pub fn installed_binary_path() -> PathBuf {
    PathBuf::from(INSTALLED_BINARY_PATH)
}

/// The parent of every path under `/Library/Application Support/kintsugi-agent` (config,
/// identity, queue, daemon log, check-in schedule) — a full removal (`self_removal`) deletes this
/// one directory rather than each file individually.
pub fn config_dir() -> PathBuf {
    PathBuf::from("/Library/Application Support/kintsugi-agent")
}

/// Where the `--agent` (per-user) process keeps its own state: the cached patching policy and
/// scheduling state (next due time, delays used). Lives under the invoking user's home directory
/// since, unlike the root daemon's config, this process never runs as root.
///
/// Doesn't rely on `$HOME` alone: the installer loads this process via `launchctl bootstrap
/// gui/<uid>` from a root context (to pick it up immediately on a reinstall, without requiring a
/// log out/in), and a job started that way isn't guaranteed the same environment a normal login
/// session gets — `$HOME` in particular has been observed missing entirely in that case, which
/// previously made this fail silently before the agent's own log file even existed to explain
/// why. Falling back to the current effective user's actual passwd entry sidesteps environment
/// variables altogether.
pub fn user_state_dir() -> Result<PathBuf> {
    let home = std::env::var("HOME")
        .ok()
        .filter(|v| !v.trim().is_empty())
        .map(PathBuf::from)
        .or_else(home_dir_from_passwd)
        .context("could not determine the current user's home directory (checked $HOME and the passwd database)")?;

    Ok(home.join("Library/Application Support/kintsugi-agent"))
}

fn home_dir_from_passwd() -> Option<PathBuf> {
    // SAFETY: getuid() cannot fail; getpwuid() returns either a valid pointer into a
    // thread-local static buffer (which we only read from, immediately, before any other libc
    // call in this thread could invalidate it) or null.
    unsafe {
        let passwd = libc::getpwuid(libc::getuid());
        if passwd.is_null() {
            return None;
        }
        let home_dir = CStr::from_ptr((*passwd).pw_dir).to_str().ok()?;
        if home_dir.is_empty() {
            return None;
        }
        Some(PathBuf::from(home_dir))
    }
}
