use std::collections::{HashMap, HashSet};
use std::fs;
use std::net::UdpSocket;
use std::path::{Path, PathBuf};
use std::process::Command;

use anyhow::{Context, Result};
use serde::Serialize;

#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize)]
pub struct InstalledApp {
    pub name: String,
    pub version: String,
    /// Name of another reported app that manages this one (e.g. "Homebrew"
    /// for a formula/cask). Must match that app's own `name` exactly.
    #[serde(rename = "packageManager", skip_serializing_if = "Option::is_none")]
    pub package_manager: Option<String>,
    /// The app bundle's `CFBundleIdentifier` (e.g. "com.example.MyApp").
    /// Not available for Homebrew-sourced entries.
    #[serde(rename = "applicationIdentifier", skip_serializing_if = "Option::is_none")]
    pub application_identifier: Option<String>,
    /// The latest version available, when known independently of any
    /// upgrade research — currently only for Homebrew formulae/casks, read
    /// straight from Homebrew's own catalog. Lets the backend tell whether
    /// an update is available without having to research it separately.
    #[serde(rename = "availableVersion", skip_serializing_if = "Option::is_none")]
    pub available_version: Option<String>,
}

/// Returns the machine's local hostname (e.g. "laptop-jsmith.local").
pub fn hostname() -> Result<String> {
    gethostname::gethostname()
        .into_string()
        .map_err(|raw| anyhow::anyhow!("hostname is not valid UTF-8: {raw:?}"))
}

/// Returns the hardware serial number by shelling out to `system_profiler`,
/// which is the standard, documented way to read it on macOS without
/// requiring elevated entitlements.
pub fn serial_number() -> Result<String> {
    let output = Command::new("system_profiler")
        .args(["SPHardwareDataType", "-json"])
        .output()
        .context("failed to run system_profiler")?;

    if !output.status.success() {
        anyhow::bail!(
            "system_profiler exited with status {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr)
        );
    }

    let json: serde_json::Value =
        serde_json::from_slice(&output.stdout).context("failed to parse system_profiler JSON output")?;

    json["SPHardwareDataType"][0]["serial_number"]
        .as_str()
        .map(str::to_string)
        .context("serial_number field missing from system_profiler output")
}

/// Returns a human-readable OS name and version, e.g. "macOS 14.5".
pub fn operating_system() -> Result<String> {
    let name = run_sw_vers("-productName")?;
    let version = run_sw_vers("-productVersion")?;
    Ok(format!("{name} {version}"))
}

fn run_sw_vers(flag: &str) -> Result<String> {
    let output = Command::new("sw_vers")
        .arg(flag)
        .output()
        .with_context(|| format!("failed to run sw_vers {flag}"))?;

    if !output.status.success() {
        anyhow::bail!(
            "sw_vers {flag} exited with status {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr)
        );
    }

    Ok(String::from_utf8_lossy(&output.stdout).trim().to_string())
}

/// Returns the local IP address this machine would use to route outbound
/// traffic. Uses a UDP "connect" (no packets are actually sent — it only
/// resolves the local route) so it works the same whether the target is
/// reachable or not, and needs no extra permissions.
pub fn local_ip_address() -> Result<String> {
    let socket = UdpSocket::bind("0.0.0.0:0").context("failed to bind local UDP socket")?;
    socket
        .connect("8.8.8.8:80")
        .context("failed to resolve local outbound route")?;
    let addr = socket.local_addr().context("failed to read local socket address")?;
    Ok(addr.ip().to_string())
}

/// Scans the top level of /Applications for .app bundles, reading each
/// one's Info.plist for its display name and version. Bundles owned by
/// Apple (bundle identifier starting with "com.apple.") are skipped, since
/// they're part of the OS rather than something worth patch-tracking.
/// Individual unreadable bundles are skipped with a warning rather than
/// failing the whole scan.
///
/// `cask_app_bundle_names` (from [`HomebrewScan::cask_app_bundle_names`])
/// are also skipped: a cask-installed app lives under /Applications like
/// any other, but it's already reported as Homebrew-managed by
/// [`scan_homebrew`], so re-reporting it here would register it a second
/// time as an unmanaged, standalone application.
pub fn scan_applications_folder(cask_app_bundle_names: &HashSet<String>) -> Vec<InstalledApp> {
    scan_applications_folder_at(Path::new("/Applications"), cask_app_bundle_names)
}

fn scan_applications_folder_at(dir: &Path, cask_app_bundle_names: &HashSet<String>) -> Vec<InstalledApp> {
    let entries = match fs::read_dir(dir) {
        Ok(entries) => entries,
        Err(err) => {
            crate::logging::warn(&format!("could not read {}: {err}", dir.display()));
            return Vec::new();
        }
    };

    let mut apps = Vec::new();

    for entry in entries.flatten() {
        let path = entry.path();
        if path.extension().and_then(|ext| ext.to_str()) != Some("app") {
            continue;
        }
        if path.file_name().and_then(|name| name.to_str()).is_some_and(|name| cask_app_bundle_names.contains(name)) {
            continue;
        }

        match read_app_bundle(&path) {
            Ok(Some(app)) => apps.push(app),
            Ok(None) => {} // Apple-owned bundle, intentionally skipped
            Err(err) => crate::logging::warn(&format!("skipping {}: {err}", path.display())),
        }
    }

    apps
}

fn read_app_bundle(app_path: &Path) -> Result<Option<InstalledApp>> {
    let info_plist = app_path.join("Contents/Info.plist");
    if !info_plist.is_file() {
        anyhow::bail!("no Contents/Info.plist found");
    }

    let json = read_plist_as_json(&info_plist)?;

    let bundle_id = json["CFBundleIdentifier"].as_str().unwrap_or("");
    if bundle_id.starts_with("com.apple.") {
        return Ok(None);
    }

    let name = json["CFBundleDisplayName"]
        .as_str()
        .or_else(|| json["CFBundleName"].as_str())
        .map(str::to_string)
        .or_else(|| app_path.file_stem().and_then(|s| s.to_str()).map(str::to_string))
        .context("could not determine application name")?;

    let version = json["CFBundleShortVersionString"]
        .as_str()
        .or_else(|| json["CFBundleVersion"].as_str())
        .unwrap_or("unknown")
        .to_string();

    let application_identifier = if bundle_id.is_empty() { None } else { Some(bundle_id.to_string()) };

    Ok(Some(InstalledApp {
        name,
        version,
        package_manager: None,
        application_identifier,
        available_version: None,
    }))
}

/// Converts a plist (binary or XML) to JSON via the system `plutil` tool,
/// avoiding a dependency on a plist-parsing crate.
fn read_plist_as_json(path: &Path) -> Result<serde_json::Value> {
    let output = Command::new("plutil")
        .args(["-convert", "json", "-o", "-"])
        .arg(path)
        .output()
        .context("failed to run plutil")?;

    if !output.status.success() {
        anyhow::bail!(
            "plutil exited with status {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr)
        );
    }

    serde_json::from_slice(&output.stdout).context("failed to parse plutil JSON output")
}

/// Name reported for Homebrew's own entry, and the `packageManager` value
/// every formula/cask it manages is tagged with — the backend links a child
/// to its manager by matching this name against another entry's `name` in
/// the same report.
const HOMEBREW_NAME: &str = "Homebrew";

/// Result of [`scan_homebrew`]: the Homebrew-managed apps to report, plus
/// the set of /Applications bundle names those casks already account for.
pub struct HomebrewScan {
    pub apps: Vec<InstalledApp>,
    /// Basenames (e.g. "Slack.app") of app bundles that a cask installed.
    /// Pass this to [`scan_applications_folder`] so it doesn't also report
    /// the same app as a separate, unmanaged entry.
    pub cask_app_bundle_names: HashSet<String>,
}

/// Reports Homebrew itself (with its own version) plus every installed
/// formula and cask, each tagged as managed by "Homebrew". Returns an empty
/// scan (not an error) if Homebrew isn't installed — most Macs won't have it.
pub fn scan_homebrew() -> HomebrewScan {
    let Some(brew) = find_brew_binary() else {
        return HomebrewScan {
            apps: Vec::new(),
            cask_app_bundle_names: HashSet::new(),
        };
    };

    // Homebrew refuses to run as root outright ("Running Homebrew as root
    // is extremely dangerous and no longer supported"), which is exactly
    // how this agent normally runs (a root LaunchDaemon). When that's the
    // case, run brew as the user who actually owns the install instead —
    // the approach Homebrew itself recommends for root-run automation.
    let run_as = if running_as_root() {
        match brew_owner_username(&brew) {
            Ok(username) => Some(username),
            Err(err) => {
                crate::logging::warn(&format!("could not determine Homebrew's owner, skipping Homebrew scan: {err}"));
                return HomebrewScan {
                    apps: Vec::new(),
                    cask_app_bundle_names: HashSet::new(),
                };
            }
        }
    } else {
        None
    };

    let mut apps = Vec::new();

    match brew_own_version(&brew, run_as.as_deref()) {
        Ok(version) => apps.push(InstalledApp {
            name: HOMEBREW_NAME.to_string(),
            version,
            package_manager: None,
            application_identifier: None,
            available_version: None,
        }),
        Err(err) => crate::logging::warn(&format!("could not determine Homebrew's own version: {err}")),
    }

    let info = brew_installed_info(&brew, run_as.as_deref());

    apps.extend(list_brew_packages(&brew, run_as.as_deref(), "--formula", &info.latest_versions));
    apps.extend(list_brew_packages(&brew, run_as.as_deref(), "--cask", &info.latest_versions));

    HomebrewScan { apps, cask_app_bundle_names: info.cask_app_bundle_names }
}

/// Extra per-package detail read from `brew info`, keyed by formula name or
/// cask token, that `brew list --versions` alone doesn't provide.
struct BrewInstalledInfo {
    /// Latest version available per Homebrew's own catalog (a formula's
    /// stable version, or a cask's defined version) — not necessarily what's
    /// currently installed.
    latest_versions: HashMap<String, String>,
    /// Basenames (e.g. "Slack.app") of every app bundle an installed cask
    /// places under /Applications, read from each cask's `artifacts` list.
    cask_app_bundle_names: HashSet<String>,
}

/// Basename of `path` if it names a top-level `/Applications/*.app` bundle,
/// e.g. "/Applications/Adobe Acrobat Reader.app" -> Some("Adobe Acrobat
/// Reader.app"). Used to recognize app bundles named in a cask's `uninstall`
/// artifact (see [`brew_installed_info`]).
fn app_bundle_name_in_applications(path: &str) -> Option<String> {
    let path = Path::new(path);
    if path.parent()? != Path::new("/Applications") {
        return None;
    }
    if path.extension().and_then(|ext| ext.to_str()) != Some("app") {
        return None;
    }
    path.file_name()?.to_str().map(str::to_string)
}

/// Reads `brew info --json=v2 --installed` (covering both formulae and
/// casks in one call) for data `brew list --versions` doesn't carry: each
/// package's latest catalog version, and the app bundle names casks
/// install. Best-effort: returns empty results on any failure rather than
/// affecting the rest of the scan, since none of this is load-bearing on
/// its own — it only enriches entries `list_brew_packages` already reports.
///
/// A cask's app bundle name is read from its `app` artifact when present,
/// but plenty of casks (e.g. Adobe Acrobat Reader) install via a `.pkg`
/// installer instead and have no `app` artifact at all — their `uninstall`
/// artifact's `delete` paths are the only place the resulting
/// `/Applications/*.app` bundle name shows up, so that's checked too. Both
/// stanzas can be a bare string or an array; see [`strings_in`] for why
/// reading only the array form is a security-relevant bug rather than a
/// missed optimization.
fn brew_installed_info(brew: &Path, run_as: Option<&str>) -> BrewInstalledInfo {
    let mut command = brew_command(brew, run_as);
    command.args(["info", "--json=v2", "--installed"]);

    let empty = || BrewInstalledInfo {
        latest_versions: HashMap::new(),
        cask_app_bundle_names: HashSet::new(),
    };

    let output = match command.output() {
        Ok(output) if output.status.success() => output,
        Ok(output) => {
            crate::logging::warn(&format!(
                "`brew info --json=v2 --installed` failed: {}",
                String::from_utf8_lossy(&output.stderr)
            ));
            return empty();
        }
        Err(err) => {
            crate::logging::warn(&format!("failed to run brew: {err}"));
            return empty();
        }
    };

    match parse_brew_installed_info(&String::from_utf8_lossy(&output.stdout)) {
        Ok(info) => info,
        Err(err) => {
            crate::logging::warn(&format!("failed to parse `brew info --json=v2 --installed` output: {err}"));
            empty()
        }
    }
}

/// Every string under `value`, which Homebrew writes as a bare string when a
/// cask's stanza names one item and as an array when it names several.
///
/// Reading only the array form silently drops the single-item case, and for
/// `uninstall`'s `delete` that is not cosmetic: the dropped bundle name never
/// reaches `cask_app_bundle_names`, so [`scan_applications_folder`] stops
/// recognizing the bundle as cask-installed and reports it a *second* time as
/// a standalone application — this time carrying a `CFBundleIdentifier`. That
/// identifier is exactly what the backend's `is_patchable` requires before it
/// will run a `Script` row (see `UpgradePathRepository.GetStatusesAsync`), so
/// a Homebrew row that the per-user process cannot patch at all becomes
/// eligible for patching. It shipped that way: `nextcloud` declares
/// `delete: "/Applications/Nextcloud.app"` as a single string, so every patch
/// cycle quit Nextcloud, failed inside `brew` (a `pkg` cask needs root, which
/// this process has no way to obtain — see `upgrade::patch_one`), and left the
/// client stopped, forever.
fn strings_in(value: &serde_json::Value) -> Vec<&str> {
    match value {
        serde_json::Value::String(text) => vec![text.as_str()],
        serde_json::Value::Array(items) => items.iter().filter_map(|item| item.as_str()).collect(),
        _ => Vec::new(),
    }
}

/// The pure half of [`brew_installed_info`], split out so it can be exercised
/// against captured `brew info --json=v2 --installed` output rather than only
/// via a real (macOS-and-Homebrew-only) subprocess call — the same shape every
/// other output parser in this agent uses.
fn parse_brew_installed_info(json_text: &str) -> Result<BrewInstalledInfo> {
    let json: serde_json::Value = serde_json::from_str(json_text).context("not valid JSON")?;

    let mut latest_versions = HashMap::new();

    for formula in json["formulae"].as_array().into_iter().flatten() {
        if let (Some(name), Some(latest)) = (formula["name"].as_str(), formula["versions"]["stable"].as_str()) {
            latest_versions.insert(name.to_string(), latest.to_string());
        }
    }

    let mut cask_app_bundle_names = HashSet::new();

    for cask in json["casks"].as_array().into_iter().flatten() {
        if let (Some(token), Some(latest)) = (cask["token"].as_str(), cask["version"].as_str()) {
            latest_versions.insert(token.to_string(), latest.to_string());
        }

        for artifact in cask["artifacts"].as_array().into_iter().flatten() {
            cask_app_bundle_names.extend(strings_in(&artifact["app"]).into_iter().map(str::to_string));

            let deleted_apps = artifact["uninstall"]
                .as_array()
                .into_iter()
                .flatten()
                .flat_map(|entry| strings_in(&entry["delete"]))
                .filter_map(app_bundle_name_in_applications);
            cask_app_bundle_names.extend(deleted_apps);
        }
    }

    Ok(BrewInstalledInfo { latest_versions, cask_app_bundle_names })
}

/// Homebrew installs to a fixed prefix depending on CPU architecture
/// (Apple Silicon vs Intel) and isn't necessarily on PATH — especially not
/// for a root LaunchDaemon, which gets a minimal system PATH regardless of
/// the logged-in user's shell configuration.
fn find_brew_binary() -> Option<PathBuf> {
    ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"]
        .into_iter()
        .map(PathBuf::from)
        .find(|path| path.is_file())
}

fn running_as_root() -> bool {
    Command::new("id")
        .arg("-u")
        .output()
        .ok()
        .filter(|output| output.status.success())
        .is_some_and(|output| String::from_utf8_lossy(&output.stdout).trim() == "0")
}

/// Resolves the username that owns the `brew` binary, i.e. whoever
/// installed Homebrew.
fn brew_owner_username(brew: &Path) -> Result<String> {
    use std::os::unix::fs::MetadataExt;

    let uid = fs::metadata(brew)
        .with_context(|| format!("failed to stat {}", brew.display()))?
        .uid();

    let output = Command::new("id")
        .args(["-un", &uid.to_string()])
        .output()
        .context("failed to run id")?;

    if !output.status.success() {
        anyhow::bail!("id -un {uid} exited with status {}", output.status);
    }

    Ok(String::from_utf8_lossy(&output.stdout).trim().to_string())
}

/// Builds a `brew` invocation, transparently wrapped in `sudo -u <run_as>`
/// when Homebrew must be run as a different (non-root) user.
fn brew_command(brew: &Path, run_as: Option<&str>) -> Command {
    match run_as {
        Some(username) => {
            let mut cmd = Command::new("/usr/bin/sudo");
            cmd.args(["-u", username, "-H"]).arg(brew);
            cmd
        }
        None => Command::new(brew),
    }
}

/// Parses Homebrew's own version from `brew --version`, whose first line
/// looks like "Homebrew 4.3.9".
fn brew_own_version(brew: &Path, run_as: Option<&str>) -> Result<String> {
    let output = brew_command(brew, run_as).arg("--version").output().context("failed to run brew --version")?;

    if !output.status.success() {
        anyhow::bail!(
            "brew --version exited with status {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr)
        );
    }

    String::from_utf8_lossy(&output.stdout)
        .lines()
        .next()
        .and_then(|line| line.split_whitespace().nth(1))
        .map(str::to_string)
        .context("unexpected `brew --version` output format")
}

fn list_brew_packages(brew: &Path, run_as: Option<&str>, kind: &str, latest_versions: &HashMap<String, String>) -> Vec<InstalledApp> {
    let mut command = brew_command(brew, run_as);
    command.args(["list", kind, "--versions"]);

    let output = match command.output() {
        Ok(output) if output.status.success() => output,
        Ok(output) => {
            crate::logging::warn(&format!(
                "`brew list {kind} --versions` failed: {}",
                String::from_utf8_lossy(&output.stderr)
            ));
            return Vec::new();
        }
        Err(err) => {
            crate::logging::warn(&format!("failed to run brew: {err}"));
            return Vec::new();
        }
    };

    String::from_utf8_lossy(&output.stdout)
        .lines()
        .filter_map(|line| {
            // Each line is "<name> <version>" for casks, or
            // "<name> <version1> [<version2> ...]" for formulae when more
            // than one version is kept side by side — take the last as the
            // most recently installed.
            let mut tokens = line.split_whitespace();
            let name = tokens.next()?.to_string();
            let version = tokens.last()?.to_string();
            let available_version = latest_versions.get(&name).cloned();
            Some(InstalledApp {
                name,
                version,
                package_manager: Some(HOMEBREW_NAME.to_string()),
                application_identifier: None,
                available_version,
            })
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Trimmed from real `brew info --json=v2 --installed` output on a Mac
    /// running the fleet's own agent. Keeps the three shapes that matter: a
    /// formula (for `latest_versions`), an `app` cask, and a `pkg` cask whose
    /// `uninstall` stanza names a single path — which Homebrew writes as a
    /// bare string rather than a one-element array.
    const BREW_INFO_JSON: &str = r#"{
      "formulae": [
        { "name": "jq", "versions": { "stable": "1.7.1" } }
      ],
      "casks": [
        {
          "token": "rectangle",
          "version": "1.100",
          "artifacts": [
            { "uninstall": [ { "quit": "com.knollsoft.Rectangle", "login_item": "Rectangle" } ] },
            { "app": [ "Rectangle.app" ], "target": "/Applications/Rectangle.app" }
          ]
        },
        {
          "token": "microsoft-teams",
          "version": "26.1.0",
          "artifacts": [
            {
              "uninstall": [
                {
                  "delete": [
                    "/Applications/Microsoft Teams.app",
                    "/Library/Preferences/com.microsoft.teams.plist"
                  ]
                }
              ]
            }
          ]
        },
        {
          "token": "nextcloud",
          "version": "34.0.3",
          "artifacts": [
            {
              "uninstall": [
                {
                  "launchctl": "com.nextcloud.desktopclient",
                  "quit": "com.nextcloud.desktopclient",
                  "pkgutil": "com.nextcloud.desktopclient",
                  "delete": "/Applications/Nextcloud.app"
                }
              ]
            },
            { "pkg": [ "Nextcloud-34.0.3.pkg" ] }
          ]
        }
      ]
    }"#;

    #[test]
    fn parse_brew_installed_info_reads_latest_versions_for_formulae_and_casks() {
        let info = parse_brew_installed_info(BREW_INFO_JSON).expect("should parse");

        assert_eq!(info.latest_versions.get("jq").map(String::as_str), Some("1.7.1"));
        assert_eq!(info.latest_versions.get("nextcloud").map(String::as_str), Some("34.0.3"));
    }

    #[test]
    fn parse_brew_installed_info_takes_bundle_names_from_the_app_stanza() {
        let info = parse_brew_installed_info(BREW_INFO_JSON).expect("should parse");

        assert!(info.cask_app_bundle_names.contains("Rectangle.app"));
    }

    #[test]
    fn parse_brew_installed_info_takes_bundle_names_from_a_multi_path_uninstall_delete() {
        let info = parse_brew_installed_info(BREW_INFO_JSON).expect("should parse");

        // A `pkg` cask has no `app` stanza at all, so its `uninstall`'s
        // `delete` paths are the only place the bundle name appears.
        assert!(info.cask_app_bundle_names.contains("Microsoft Teams.app"));
    }

    #[test]
    fn parse_brew_installed_info_takes_bundle_names_from_a_single_path_uninstall_delete() {
        let info = parse_brew_installed_info(BREW_INFO_JSON).expect("should parse");

        // The regression this test exists for: `nextcloud` names one path, so
        // Homebrew writes `delete` as a bare string. Reading only the array
        // form dropped it, `/Applications/Nextcloud.app` escaped the dedup in
        // `scan_applications_folder`, and it was reported a second time as a
        // standalone application carrying a bundle identifier — which is what
        // made an unpatchable Homebrew row look patchable. See `strings_in`.
        assert!(
            info.cask_app_bundle_names.contains("Nextcloud.app"),
            "single-path `delete` was dropped: {:?}",
            info.cask_app_bundle_names
        );
    }

    #[test]
    fn parse_brew_installed_info_ignores_paths_outside_applications() {
        let info = parse_brew_installed_info(BREW_INFO_JSON).expect("should parse");

        // `/Library/Preferences/com.microsoft.teams.plist` sits beside a real
        // bundle path in the same `delete` array and must not be mistaken for
        // one — nor must the `pkg`/`binary` stanzas contribute anything.
        assert_eq!(
            info.cask_app_bundle_names,
            ["Rectangle.app", "Microsoft Teams.app", "Nextcloud.app"]
                .into_iter()
                .map(str::to_string)
                .collect::<HashSet<String>>()
        );
    }

    #[test]
    fn parse_brew_installed_info_reports_an_error_for_unparseable_output() {
        // `brew_installed_info` turns this into an empty (not partial) result,
        // so a Homebrew whose JSON shape changes degrades to "no enrichment"
        // rather than to a wrong dedup set.
        assert!(parse_brew_installed_info("not json at all").is_err());
    }

    #[test]
    fn app_bundle_name_in_applications_accepts_only_top_level_bundles() {
        assert_eq!(app_bundle_name_in_applications("/Applications/Nextcloud.app").as_deref(), Some("Nextcloud.app"));
        assert_eq!(app_bundle_name_in_applications("/Applications/Nextcloud.app/Contents/MacOS/nextcloudcmd"), None);
        assert_eq!(app_bundle_name_in_applications("/Applications/Utilities/Foo.app"), None);
        assert_eq!(app_bundle_name_in_applications("/Applications/DisplayLink"), None);
        assert_eq!(app_bundle_name_in_applications("/Library/Preferences/com.example.plist"), None);
    }
}
