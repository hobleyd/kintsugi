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
    /// Whatever stably names this application: the app bundle's `CFBundleIdentifier` (e.g.
    /// "com.example.MyApp"), or a Homebrew formula name / cask token. The backend's
    /// `is_patchable` refuses to run a `Script` row without one, so leaving it unset is how an
    /// entry says "do not try" — which is what a cask whose upgrade needs root gets when it cannot
    /// be handed to the root daemon instead, see [`cask_requires_root`] and
    /// [`pkg_casks_leaving_homebrew`].
    #[serde(rename = "applicationIdentifier", skip_serializing_if = "Option::is_none")]
    pub application_identifier: Option<String>,
    /// The latest version available, when known independently of any
    /// upgrade research — currently only for Homebrew formulae/casks, read
    /// straight from Homebrew's own catalog. Lets the backend tell whether
    /// an update is available without having to research it separately.
    #[serde(rename = "availableVersion", skip_serializing_if = "Option::is_none")]
    pub available_version: Option<String>,
    /// The manager's own verdict on whether an update is pending, independent of any version
    /// string. Mirrors `ApplicationEntry.UpdateAvailable` on the server and the same field in the
    /// Linux agent, which is the only one that sets it: Flatpak and Snap both ship rebuilds under
    /// an unchanged version (and Flatpak often has no version to print at all), so their verdict
    /// has to travel separately or the server's version comparison calls them current. Homebrew
    /// has no such case — `brew outdated` only ever names a formula or cask whose version string
    /// changed, so `available_version` already carries the verdict — and this stays `None`.
    #[serde(rename = "updateAvailable", skip_serializing_if = "Option::is_none")]
    pub update_available: Option<bool>,
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

/// Where installed software is looked for. Three places, each a directory of bundles whose
/// `Contents/Info.plist` names and versions them, and nothing else: reading a bundle is the one
/// inventory primitive this agent has, so "where the scan looks" is exactly the list of places
/// [`scanned_bundle_path`] accepts, and the two are kept in step by that function being the only
/// definition.
///
/// - `/Applications/*.app`, the ordinary case.
/// - `/Applications/<Folder>/*.app`, one folder deep and no deeper. Vendors that ship several
///   applications put them in a folder (`Adobe Acrobat DC/`, `NinjaRemote/`), and Adobe's own
///   updater *moves* Acrobat Reader from the top level into one — so a Mac on which the scan
///   stopped at the top level reported Reader while the cask still described it and lost it the
///   day Adobe moved it. A bundle's own `Contents/` is a folder too, which is why the parent may
///   not itself be a `.app`.
/// - `/Library/Java/JavaVirtualMachines/*.jdk`. A JDK is a bundle with an `Info.plist` like any
///   other, installed root-owned by the vendor's `.pkg` — Temurin, Zulu, Corretto, Oracle — and
///   is patched the same way, but lives nowhere near /Applications. See [`read_app_bundle`] for
///   how one is named.
const APPLICATIONS_DIR: &str = "/Applications";
const JAVA_VIRTUAL_MACHINES_DIR: &str = "/Library/Java/JavaVirtualMachines";

/// Scans every place listed above for bundles, reading each one's Info.plist for its display name
/// and version. Bundles owned by Apple (bundle identifier starting with "com.apple.") are
/// skipped, since they're part of the OS rather than something worth patch-tracking — unless
/// they carry an App Store receipt, see below. Individual unreadable bundles are skipped with a
/// warning rather than failing the whole scan.
///
/// `cask_bundle_paths` (from [`HomebrewScan::cask_bundle_paths`]) are also skipped: a
/// cask-installed app lives under /Applications like any other, but it's already reported as
/// Homebrew-managed by [`scan_homebrew`], so re-reporting it here would register it a second
/// time as an unmanaged, standalone application.
///
/// A bundle installed from the Mac App Store is reported as managed by
/// [`APP_STORE_NAME`] rather than as a standalone application, and the App
/// Store itself is reported once as their manager whenever there is at least
/// one — see [`read_app_bundle`] for why that distinction matters.
pub fn scan_installed_bundles(cask_bundle_paths: &HashSet<PathBuf>) -> Vec<InstalledApp> {
    let mut apps = scan_applications_tree(Path::new(APPLICATIONS_DIR), cask_bundle_paths);
    apps.extend(scan_bundles_at(Path::new(JAVA_VIRTUAL_MACHINES_DIR), "jdk", cask_bundle_paths));

    if apps.iter().any(|app| app.package_manager.as_deref() == Some(APP_STORE_NAME)) {
        match app_store_version() {
            Ok(version) => apps.push(InstalledApp {
                name: APP_STORE_NAME.to_string(),
                version,
                package_manager: None,
                // The App Store is part of macOS and updates with it (see `os_update`), so its own
                // row has nothing to patch; the server's AppStoreUpgradeScript declines to answer a
                // version for it for the same reason, and no identifier keeps `is_patchable` false.
                application_identifier: None,
                available_version: None,
                update_available: None,
            }),
            Err(err) => crate::logging::warn(&format!("could not determine the App Store's own version: {err}")),
        }
    }

    apps
}

/// `/Applications` and its immediate sub-folders — see [`APPLICATIONS_DIR`] for why exactly one
/// level. Split from [`scan_installed_bundles`] so it can be run on a scratch tree.
fn scan_applications_tree(root: &Path, cask_bundle_paths: &HashSet<PathBuf>) -> Vec<InstalledApp> {
    let mut apps = scan_bundles_at(root, "app", cask_bundle_paths);

    let Ok(entries) = fs::read_dir(root) else { return apps };
    for entry in entries.flatten() {
        let path = entry.path();
        let is_plain_folder = path.is_dir() && path.extension().is_none_or(|ext| ext != "app");
        if is_plain_folder {
            apps.extend(scan_bundles_at(&path, "app", cask_bundle_paths));
        }
    }
    apps
}

/// Every `*.<extension>` bundle directly inside `dir`, minus the ones a cask accounts for. A
/// missing directory is an ordinary answer — most Macs have no `/Library/Java` at all — and only
/// a directory that exists but cannot be read is worth a warning.
fn scan_bundles_at(dir: &Path, extension: &str, cask_bundle_paths: &HashSet<PathBuf>) -> Vec<InstalledApp> {
    if !dir.exists() {
        return Vec::new();
    }
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
        if path.extension().and_then(|ext| ext.to_str()) != Some(extension) {
            continue;
        }
        if cask_bundle_paths.contains(&path) {
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

/// Name reported for the Mac App Store's own entry, and the `packageManager` value every
/// application installed from it is tagged with — the backend links a child to its manager by
/// matching this name against another entry's `name` in the same report, and
/// `PackageManagerCatalog.AppStore` on the server recognizes this exact string. Its
/// `AppStoreUpgradeScript` tells the manager's own row apart from its children by this name at
/// runtime, the way `HomebrewUpgradeScript` does with [`HOMEBREW_NAME`].
pub const APP_STORE_NAME: &str = "App Store";

/// The App Store's own version, read from the bundle macOS ships it in.
fn app_store_version() -> Result<String> {
    let json = read_plist_as_json(Path::new("/System/Applications/App Store.app/Contents/Info.plist"))?;
    json["CFBundleShortVersionString"]
        .as_str()
        .map(str::to_string)
        .context("App Store's Info.plist has no CFBundleShortVersionString")
}

/// A bundle that came from the Mac App Store carries the store's receipt at
/// `Contents/_MASReceipt/receipt`; nothing else does, and `/System/Applications/*` — the bundles
/// that actually are part of the OS — never has one. That file decides two things here.
///
/// The first is who manages the application. Reported as a standalone bundle, an App Store app
/// goes to the AI research flow, whose macOS prompt (`AiUpgradePathResearchClient`) assumes a
/// Developer-ID distribution and writes a script that downloads the vendor's DMG and replaces the
/// bundle in place — swapping an App Store build for a direct-download one, with its receipt,
/// sandbox container and any in-app purchases gone, and the App Store no longer recognizing it.
/// Signed and approved, that script runs as root through the queue and nothing errors. So the
/// receipt routes the bundle to the `App Store` manager and the server's fixed
/// `AppStoreUpgradeScript` instead, which knows exactly where it came from.
///
/// The second is whether `com.apple.` means "part of the OS". It doesn't for Xcode, Pages,
/// Numbers, Keynote, iMovie or GarageBand — Apple-authored, but sold through the store and updated
/// by it, not by `softwareupdate` — and skipping them by prefix left a Mac with four of them out of
/// date reporting nothing. The receipt tells the two kinds of Apple bundle apart.
///
/// A VPP-licensed bundle (installed by an MDM's device-based assignment, rather than under a
/// person's Apple Account) is reported under the same manager but with no `application_identifier`,
/// which is how an entry says "do not try" (see [`InstalledApp::application_identifier`]): the MDM
/// owns those installations, and the App Store cannot update one under any Apple Account.
///
/// **A `.jdk` bundle is named by its directory, not its `Info.plist`.** Temurin 26's plist says
/// `CFBundleName` "OpenJDK 26.0.2.1" — the version baked into the name, so every update release
/// would arrive as a new application — and `CFBundleIdentifier` `net.java.openjdk.jdk`, which
/// every OpenJDK build from every vendor and every major shares. The directory is what the
/// vendor's installer names the *feature line*: `temurin-26.jdk`, `zulu-21.jdk`,
/// `amazon-corretto-21.jdk`, Oracle's `jdk-21.jdk`. It stays put across update releases (26.0.2
/// replaces 26.0.1 in the same `temurin-26.jdk`) and distinguishes vendors and majors from one
/// another, which is what a row that is patched to "the latest 26" needs. The server's macOS
/// prompt tells the script to find a JDK at `/Library/Java/JavaVirtualMachines/<appName>.jdk`,
/// so the stem is the whole of the contract. The identifier stays the plist's: it is what the
/// script checks before touching anything, and shared or not, it is the bundle's own.
fn read_app_bundle(app_path: &Path) -> Result<Option<InstalledApp>> {
    let info_plist = app_path.join("Contents/Info.plist");
    if !info_plist.is_file() {
        anyhow::bail!("no Contents/Info.plist found");
    }

    let json = read_plist_as_json(&info_plist)?;

    let bundle_id = json["CFBundleIdentifier"].as_str().unwrap_or("");
    let from_app_store = app_path.join(APP_STORE_RECEIPT).is_file();
    if bundle_id.starts_with("com.apple.") && !from_app_store {
        return Ok(None);
    }

    let directory_stem = app_path.file_stem().and_then(|s| s.to_str()).map(str::to_string);
    let is_jdk = app_path.extension().and_then(|ext| ext.to_str()) == Some("jdk");
    let name = if is_jdk {
        directory_stem.context("a JDK bundle without a directory name")?
    } else {
        json["CFBundleDisplayName"]
            .as_str()
            .or_else(|| json["CFBundleName"].as_str())
            .map(str::to_string)
            .or(directory_stem)
            .context("could not determine application name")?
    };

    let version = json["CFBundleShortVersionString"]
        .as_str()
        .or_else(|| json["CFBundleVersion"].as_str())
        .unwrap_or("unknown")
        .to_string();

    // The bundle identifier is what both ends of the App Store script key on: the server's
    // `--update-version` looks it up with `lookup?bundleId=`, and `mas` accepts it in place of the
    // numeric ADAM ID. It needs no Spotlight index, unlike `kMDItemAppStoreAdamID`.
    let application_identifier = if bundle_id.is_empty() || (from_app_store && vpp_licensed(app_path)) {
        None
    } else {
        Some(bundle_id.to_string())
    };

    Ok(Some(InstalledApp {
        name,
        version,
        package_manager: from_app_store.then(|| APP_STORE_NAME.to_string()),
        application_identifier,
        available_version: None,
        update_available: None,
    }))
}

/// Where the Mac App Store leaves its receipt inside a bundle it installed. The same path `mas`
/// copies a fresh receipt to after an update.
const APP_STORE_RECEIPT: &str = "Contents/_MASReceipt/receipt";

/// Whether Spotlight records this App Store bundle as installed under a Volume Purchase Program
/// licence — an MDM's device-based assignment — rather than under a person's Apple Account. Read
/// through `mdls` because the receipt itself is a PKCS#7 blob and parsing it would need a crate for
/// a single boolean. Best-effort in the safe direction: an unindexed bundle answers `(null)`, which
/// is read as "not VPP", and the worst that costs is a row that looks patchable and whose update
/// the App Store then refuses with a clear message — where the other error would silently keep a
/// patchable application out of every cycle.
fn vpp_licensed(app_path: &Path) -> bool {
    let output = Command::new("/usr/bin/mdls")
        .args(["-raw", "-name", "kMDItemAppStoreReceiptIsVPPLicensed"])
        .arg(app_path)
        .output();
    match output {
        Ok(output) if output.status.success() => parse_mdls_bool(&String::from_utf8_lossy(&output.stdout)) == Some(true),
        Ok(output) => {
            crate::logging::warn(&format!(
                "mdls failed for {}: {}",
                app_path.display(),
                String::from_utf8_lossy(&output.stderr).trim()
            ));
            false
        }
        Err(err) => {
            crate::logging::warn(&format!("could not run mdls for {}: {err}", app_path.display()));
            false
        }
    }
}

/// The pure half of [`vpp_licensed`]: `mdls -raw` prints a boolean attribute as `1` or `0`, and
/// `(null)` when the attribute is absent or the bundle is not indexed.
fn parse_mdls_bool(raw: &str) -> Option<bool> {
    match raw.trim() {
        "1" => Some(true),
        "0" => Some(false),
        _ => None,
    }
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
/// the same report. `upgrade::runs_as_root` recognizes the manager's own row
/// by it too, since that row carries no `packageManager` of its own.
pub const HOMEBREW_NAME: &str = "Homebrew";

/// Result of [`scan_homebrew`]: the Homebrew-managed apps to report, plus
/// the set of bundle paths those casks already account for.
pub struct HomebrewScan {
    pub apps: Vec<InstalledApp>,
    /// Paths (e.g. "/Applications/Slack.app") of bundles that a cask installed.
    /// Pass this to [`scan_installed_bundles`] so it doesn't also report
    /// the same app as a separate, unmanaged entry.
    pub cask_bundle_paths: HashSet<PathBuf>,
}

/// Reports Homebrew itself (with its own version) plus every installed
/// formula and cask, each tagged as managed by "Homebrew". Returns an empty
/// scan (not an error) if Homebrew isn't installed — most Macs won't have it.
pub fn scan_homebrew() -> HomebrewScan {
    let Some(brew) = find_brew_binary() else {
        return HomebrewScan {
            apps: Vec::new(),
            cask_bundle_paths: HashSet::new(),
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
                    cask_bundle_paths: HashSet::new(),
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
            // Homebrew's own row runs the same shared script every formula does (`brew update` is
            // Homebrew upgrading itself — see HomebrewUpgradeScript on the server), as the same
            // user, so it is as patchable as they are — and `upgrade::runs_as_root` keeps it with
            // that user by name, since `package_manager: None` alone reads as AI-researched.
            application_identifier: Some("brew".to_string()),
            available_version: None,
            update_available: None,
        }),
        Err(err) => crate::logging::warn(&format!("could not determine Homebrew's own version: {err}")),
    }

    let mut info = brew_installed_info(&brew, run_as.as_deref());

    // A `pkg` cask whose bundle the scan can see leaves Homebrew here, before the listing below,
    // so this very report already shows it as the standalone bundle the scan finds rather than as
    // a Homebrew row nothing can patch — see `pkg_casks_leaving_homebrew` for the reasoning and
    // `forget_cask` for what "leaves" means.
    let leaving = pkg_casks_leaving_homebrew(&info, &receipt_bundle_paths, &bundle_exists);
    for cask in &leaving {
        let display = |paths: &[PathBuf]| paths.iter().map(|path| path.display().to_string()).collect::<Vec<_>>().join(", ");
        if cask.bundles_on_disk.is_empty() {
            crate::logging::info(&format!(
                "the cask '{}' has a root-owned installer and none of what it installed ({}) is still where it was put; \
                 taking it out of Homebrew's records, which described an installation that is no longer there",
                cask.token,
                display(&cask.bundles_missing)
            ));
        } else {
            crate::logging::info(&format!(
                "the cask '{}' installs {} with a root-owned installer, which Homebrew cannot upgrade from this agent; \
                 taking it out of Homebrew's records so the bundle is patched directly, as root, instead",
                cask.token,
                display(&cask.bundles_on_disk)
            ));
        }
        if let Err(err) = forget_cask(&brew, run_as.as_deref(), &cask.token) {
            // The report is right either way — this cask is omitted from the Homebrew rows and its
            // bundle is reported standalone regardless — so Homebrew merely keeps a stale record
            // that the next scan tries to drop again.
            crate::logging::warn(&format!("could not take the cask '{}' out of Homebrew's records: {err:#}", cask.token));
        }
        info.leave_homebrew(&cask.token, &cask.bundles_on_disk);
    }

    apps.extend(list_brew_packages(&brew, run_as.as_deref(), "--formula", &info));
    apps.extend(list_brew_packages(&brew, run_as.as_deref(), "--cask", &info));

    HomebrewScan { apps, cask_bundle_paths: info.cask_bundle_paths }
}

/// Extra per-package detail read from `brew info`, keyed by formula name or
/// cask token, that `brew list --versions` alone doesn't provide.
#[derive(Debug, Default)]
struct BrewInstalledInfo {
    /// Latest version available per Homebrew's own catalog (a formula's
    /// stable version, or a cask's defined version) — not necessarily what's
    /// currently installed.
    latest_versions: HashMap<String, String>,
    /// The newest keg installed per formula — the last entry of `installed`,
    /// which Homebrew sorts by version. `brew list --versions` prints the same
    /// kegs in directory order, which is *not* newest-last: with 3.6.3 and
    /// 3.6.4 of `openssl@3` both present it printed `openssl@3 3.6.4 3.6.3`, so
    /// taking its last token reported the host as still on 3.6.3, the server
    /// kept counting it behind, and every cycle ran the script to be told
    /// "Already up-to-date". The newest keg rather than `linked_keg` because
    /// that is what `brew outdated` (and so `brew upgrade`) compares against;
    /// a keg-only formula has no linked keg at all. Casks have one installed
    /// version and are not here.
    installed_versions: HashMap<String, String>,
    /// Paths (e.g. "/Applications/Slack.app") of every bundle an installed cask places where the
    /// scan looks (see [`scanned_bundle_path`]), read from each cask's `artifacts` list.
    cask_bundle_paths: HashSet<PathBuf>,
    /// Tokens of the installed casks whose `brew upgrade` would need root — see
    /// [`cask_requires_root`]. These are reported without an
    /// `application_identifier`, which is what keeps them off the patch list — unless they are
    /// among the `pkg_casks` below and leave Homebrew altogether.
    root_required_casks: HashSet<String>,
    /// Every installed cask with a `pkg`/`installer` artifact, keyed by token: what its own
    /// stanzas say about where its application lands. The candidates for
    /// [`pkg_casks_leaving_homebrew`].
    pkg_casks: HashMap<String, PkgCask>,
    /// Tokens `scan_homebrew` decided are leaving Homebrew in this scan. `parse_brew_list` omits
    /// them whether or not [`forget_cask`] managed to remove the record, so the report's shape
    /// never depends on that `rm` — the bundle is reported standalone by the folder scan instead.
    casks_left: HashSet<String>,
}

impl BrewInstalledInfo {
    /// Records that `token` is leaving Homebrew: its bundles are no longer accounted for by a cask
    /// (so [`scan_installed_bundles`] reports them), it is no longer a root-required Homebrew
    /// row, and [`parse_brew_list`] drops it.
    fn leave_homebrew(&mut self, token: &str, bundle_paths: &[PathBuf]) {
        for path in bundle_paths {
            self.cask_bundle_paths.remove(path);
        }
        self.root_required_casks.remove(token);
        self.casks_left.insert(token.to_string());
    }
}

/// What a `pkg` cask's stanzas say about the application its installer places. Both are hints,
/// not truth: a receipt is the record of what the installer *actually* wrote (see
/// [`receipt_bundle_paths`]), and the disk is what says whether it is still there.
#[derive(Debug, Default, Clone, PartialEq, Eq)]
struct PkgCask {
    /// Bundle paths (e.g. "/Applications/Nextcloud.app") the cask itself names somewhere the scan
    /// looks — its `app` artifact (at its `target`, or under /Applications), or the bundle paths
    /// of its `uninstall delete:`.
    declared_bundle_paths: Vec<PathBuf>,
    /// The receipt ids (regular expressions, as `pkgutil --pkgs=` takes them) of its
    /// `uninstall pkgutil:` stanza — what Homebrew itself would `--forget` — plus, when the
    /// installed version is not the catalog's, the same ids with the installed version's parts
    /// substituted in. See [`pkgutil_ids_for_installed_version`].
    pkgutil_ids: Vec<String>,
}

/// `path`, if it names a bundle in one of the places [`scan_installed_bundles`] looks — see
/// [`APPLICATIONS_DIR`] for the list — and `None` for anything else: a file inside a bundle, a
/// bundle two folders deep, a plain folder, a plist. This is the one definition of "where the
/// scan looks"; the cask stanzas and receipts are filtered through it so that a cask can only
/// ever be said to account for a bundle the scan would otherwise have reported.
fn scanned_bundle_path(path: &str) -> Option<PathBuf> {
    let path = Path::new(path);
    let extension = path.extension()?.to_str()?;
    let parent = path.parent()?;
    let applications = Path::new(APPLICATIONS_DIR);

    let accepted = match extension {
        "app" => parent == applications || (parent.parent() == Some(applications) && parent.extension().is_none_or(|ext| ext != "app")),
        "jdk" => parent == Path::new(JAVA_VIRTUAL_MACHINES_DIR),
        _ => false,
    };
    accepted.then(|| path.to_path_buf())
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

    let empty = BrewInstalledInfo::default;

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
/// reaches `cask_bundle_paths`, so [`scan_installed_bundles`] stops
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

/// Whether upgrading this cask would make Homebrew reach for `sudo`, which the per-user process
/// has no way to satisfy — no TTY, no `SUDO_ASKPASS` — and which `brew` itself gives no way
/// around (it refuses to run as root at all). The failure is not clean: `Cask::Pkg#uninstall`
/// pipes the old install's BOM into `sudo ... xargs rm`, sudo exits before reading it, and the
/// only thing reported is `Error: <cask>: Broken pipe`, *after* the `uninstall` stanza's `quit`
/// has already stopped the application. Every cycle then quits the app, fails, and leaves it
/// stopped — which is what happened to `nextcloud`, so such a cask is reported with no
/// `application_identifier` and never enters a patch cycle at all.
///
/// `brew upgrade` runs the cask's `uninstall` stanza and then installs the new artifacts, so both
/// halves are read. Root is needed for a `pkg` or `installer` artifact (`installer -pkg` as root),
/// and for an `uninstall` naming `pkgutil` (`pkgutil --forget` plus a root `rm` of the receipt's
/// files), `kext`, `script`, or `launchctl` — the last because Homebrew removes
/// `/Library/LaunchDaemons/<label>.plist` via sudo when it exists, and the JSON cannot say whether
/// the label is a per-user agent or a system daemon. Erring towards "needs root" costs only what
/// every cask cost before this existed (the row is not patched); erring the other way costs the
/// quit-and-fail loop above.
fn cask_requires_root(cask: &serde_json::Value) -> bool {
    const ROOT_UNINSTALL_KEYS: [&str; 4] = ["pkgutil", "kext", "script", "launchctl"];

    cask_installs_a_pkg(cask)
        || cask["artifacts"].as_array().into_iter().flatten().any(|artifact| {
            artifact["uninstall"]
                .as_array()
                .into_iter()
                .flatten()
                .any(|entry| ROOT_UNINSTALL_KEYS.iter().any(|key| !entry[key].is_null()))
        })
}

/// Whether this cask installs through a `pkg` or `installer` artifact — `installer -pkg`, run as
/// root, so everything it places is root-owned. The narrower half of [`cask_requires_root`], and
/// the only half that qualifies a cask to leave Homebrew (see [`pkg_casks_leaving_homebrew`]): a
/// cask that needs root *only* for its uninstall stanza is user-owned on disk and stays, unpatched
/// but visible, as it always has.
fn cask_installs_a_pkg(cask: &serde_json::Value) -> bool {
    const PKG_ARTIFACTS: [&str; 2] = ["pkg", "installer"];

    cask["artifacts"]
        .as_array()
        .into_iter()
        .flatten()
        .any(|artifact| PKG_ARTIFACTS.iter().any(|key| !artifact[key].is_null()))
}

/// A `pkg` cask [`pkg_casks_leaving_homebrew`] decided to take out of Homebrew, and the evidence.
#[derive(Debug, Clone, PartialEq, Eq)]
struct LeavingCask {
    token: String,
    /// The bundles this cask installed that are still where it put them, which
    /// [`scan_installed_bundles`] will now report standalone.
    bundles_on_disk: Vec<PathBuf>,
    /// The bundles its stanzas or receipts name that are *not* there any more. When
    /// `bundles_on_disk` is empty this is the whole of the evidence: Homebrew's record describes
    /// an installation that no longer exists.
    bundles_missing: Vec<PathBuf>,
}

/// The `pkg` casks this scan takes out of Homebrew, each with the bundles it leaves behind for
/// [`scan_installed_bundles`] to report. Sorted by token, so the log reads the same on every host.
///
/// A `pkg` cask is the one kind of Homebrew row this agent can never patch: `brew upgrade` has to
/// remove root-owned files, `brew` refuses to run as root, and the per-user process has no
/// password to give `sudo` and no way to get one (see [`cask_requires_root`] for the failure's
/// exact shape). The same application *not* under Homebrew is patchable — the scan reports it
/// with its `CFBundleIdentifier`, the server researches a `macOS`-bucket script that downloads
/// the vendor's `.pkg` and runs `installer -pkg ... -target /`, and `upgrade::runs_as_root` sends
/// that row to the root daemon, which is the path every standalone bundle has taken since the
/// queue existed and asks nobody for a password. So the cask leaves Homebrew and the bundle takes
/// that path. Homebrew's record was rarely right for these anyway: the installer is the vendor's
/// own, the application updates itself or is updated by hand, and the Caskroom goes on saying
/// whatever version `brew` last installed — on the Mac this was written on, Nextcloud's cask said
/// 34.0.1 while the receipt and the bundle both said 34.0.4.
///
/// What decides it is where the cask's bundle *is*, and that comes from two sources, both filtered
/// through [`scanned_bundle_path`] and both checked against the disk. The cask's own stanzas name
/// it for most casks (`app`, or `uninstall delete:`), but a `pkg` cask's stanzas describe what
/// its author believed the installer does, and they drift: `displaylink` deletes
/// `/Applications/DisplayLink` — a folder from an earlier layout — while the installer today
/// writes `DisplayLink Manager.app`. The receipt (`pkgutil --files`, via [`receipt_bundle_paths`])
/// is what the installer actually wrote, so it is consulted too, through the cask's `uninstall
/// pkgutil:` ids — as the installed version spells them, see [`pkgutil_ids_for_installed_version`],
/// which is what lets `temurin`'s `net.temurin.26.jdk` be found under a cask that now says 27.
///
/// Three outcomes, each pinned by a test:
/// - **Nothing names a bundle anywhere the scan looks:** the cask stays, unpatchable and visible,
///   as it always was. Leaving would make whatever it installed vanish from the inventory, and an
///   unpatchable row that shows the application exists is better than nothing.
/// - **A named bundle is on disk:** the cask leaves and the scan reports that bundle.
/// - **Bundles are named and none is on disk:** the cask leaves too. Homebrew is describing an
///   installation that is not there — deleted, or moved, as Adobe's updater moved Acrobat Reader
///   out of the top level into `Adobe Acrobat DC/Adobe Acrobat.app` under a new name. Nothing is
///   hidden by dropping the record (the scan already reports what is actually on disk), and
///   keeping it means a `brew upgrade` that would *reinstall* the thing and then fail.
///
/// The two probes are parameters so every one of those cases can be tested against captured
/// `brew info` output without a Caskroom or a receipt database present.
fn pkg_casks_leaving_homebrew(
    info: &BrewInstalledInfo,
    receipt_bundle_paths: &dyn Fn(&str) -> Vec<PathBuf>,
    bundle_exists: &dyn Fn(&Path) -> bool,
) -> Vec<LeavingCask> {
    let mut leaving: Vec<LeavingCask> = info
        .pkg_casks
        .iter()
        .filter_map(|(token, cask)| {
            let mut candidates = cask.declared_bundle_paths.clone();
            candidates.extend(cask.pkgutil_ids.iter().flat_map(|id| receipt_bundle_paths(id)));
            candidates.sort();
            candidates.dedup();
            if candidates.is_empty() {
                return None;
            }
            let (bundles_on_disk, bundles_missing) = candidates.into_iter().partition(|path| bundle_exists(path));
            Some(LeavingCask { token: token.clone(), bundles_on_disk, bundles_missing })
        })
        .collect();
    leaving.sort_by(|a, b| a.token.cmp(&b.token));
    leaving
}

/// Whether `path` is a bundle [`read_app_bundle`] will be able to report — the probe
/// [`pkg_casks_leaving_homebrew`] uses against the real disk.
fn bundle_exists(path: &Path) -> bool {
    path.to_str().and_then(scanned_bundle_path).is_some() && path.join("Contents/Info.plist").is_file()
}

/// The bundles, in the places the scan looks, that the receipts matching `pkgutil_id` say they
/// installed — read from `pkgutil`, the same records Homebrew's own `Cask::Pkg#uninstall` reads.
/// Best-effort in the safe direction: any failure answers no bundles, and a cask with no other
/// evidence of a bundle then stays in Homebrew.
fn receipt_bundle_paths(pkgutil_id: &str) -> Vec<PathBuf> {
    let listing = Command::new(PKGUTIL).arg(format!("--pkgs={pkgutil_id}")).output();
    let receipt_ids: Vec<String> = match listing {
        Ok(output) if output.status.success() => String::from_utf8_lossy(&output.stdout).lines().map(str::to_string).collect(),
        // `pkgutil --pkgs=<regex>` exits 1 with nothing on stdout when no receipt matches, which is
        // an ordinary answer for a cask whose stanza names a receipt this Mac never had.
        Ok(_) => return Vec::new(),
        Err(err) => {
            crate::logging::warn(&format!("could not run pkgutil for '{pkgutil_id}': {err}"));
            return Vec::new();
        }
    };

    let mut paths = Vec::new();
    for receipt_id in receipt_ids {
        let info = Command::new(PKGUTIL).args(["--pkg-info", &receipt_id]).output();
        let files = Command::new(PKGUTIL).args(["--files", &receipt_id]).output();
        match (info, files) {
            (Ok(info), Ok(files)) if info.status.success() && files.status.success() => {
                let location = receipt_location(&String::from_utf8_lossy(&info.stdout));
                paths.extend(bundle_paths_in_receipt(&location, &String::from_utf8_lossy(&files.stdout)));
            }
            _ => crate::logging::warn(&format!("could not read the receipt '{receipt_id}' from pkgutil")),
        }
    }
    paths
}

const PKGUTIL: &str = "/usr/sbin/pkgutil";

/// The `location:` line of `pkgutil --pkg-info`, relative to the volume — `Applications` for a
/// package installed into that folder, empty for one installed at the volume's root, whose file
/// list then carries the `Applications/` (or `Library/Java/...`) prefix itself.
fn receipt_location(pkg_info: &str) -> String {
    pkg_info
        .lines()
        .find_map(|line| line.strip_prefix("location:"))
        .map(|location| location.trim().trim_matches('/').to_string())
        .unwrap_or_default()
}

/// The pure half of [`receipt_bundle_paths`]: which lines of `pkgutil --files`, joined onto the
/// receipt's `location`, name a bundle somewhere the scan looks.
fn bundle_paths_in_receipt(location: &str, files: &str) -> Vec<PathBuf> {
    files
        .lines()
        .map(str::trim)
        .filter(|line| !line.is_empty())
        .map(|file| if location.is_empty() { format!("/{file}") } else { format!("/{location}/{file}") })
        .filter_map(|path| scanned_bundle_path(&path))
        .collect()
}

/// Takes `token` out of Homebrew's records without running its uninstall stanza — `brew
/// uninstall` would run it, and it is the very thing that needs the password this process does
/// not have. Homebrew holds nothing about an installed cask outside `$(brew --caskroom)/<token>`,
/// so removing that directory is exactly what `brew uninstall` itself ends with after the stanza
/// (`Cask::Installer#purge_versioned_files`), and it is the remedy Homebrew's own maintainers
/// give when a cask's uninstall is broken. The application, its receipt and anything else the
/// installer wrote are untouched. A `binary` symlink the cask made (`/opt/homebrew/bin/nextcloudcmd`
/// → into the bundle) is left too: it points at the application, not the Caskroom, and goes on
/// working.
///
/// Run as Homebrew's owner when this process is root, the way every `brew` call here is: the
/// Caskroom is that user's tree, and deleting inside it needs no more privilege than they have.
fn forget_cask(brew: &Path, run_as: Option<&str>, token: &str) -> Result<()> {
    let caskroom = brew_command(brew, run_as).arg("--caskroom").output().context("failed to run brew --caskroom")?;
    if !caskroom.status.success() {
        anyhow::bail!("brew --caskroom exited with status {}: {}", caskroom.status, String::from_utf8_lossy(&caskroom.stderr));
    }
    let caskroom = String::from_utf8_lossy(&caskroom.stdout).trim().to_string();
    if caskroom.is_empty() {
        anyhow::bail!("brew --caskroom printed nothing");
    }

    let cask_dir = Path::new(&caskroom).join(token);
    if !cask_dir.is_dir() {
        return Ok(()); // Already gone — an earlier scan removed it after reporting.
    }

    let output = owner_command(run_as, "/bin/rm").arg("-rf").arg(&cask_dir).output().context("failed to run rm")?;
    if !output.status.success() {
        anyhow::bail!("rm -rf {} exited with status {}: {}", cask_dir.display(), output.status, String::from_utf8_lossy(&output.stderr));
    }
    Ok(())
}

/// The pure half of [`brew_installed_info`], split out so it can be exercised
/// against captured `brew info --json=v2 --installed` output rather than only
/// via a real (macOS-and-Homebrew-only) subprocess call — the same shape every
/// other output parser in this agent uses.
fn parse_brew_installed_info(json_text: &str) -> Result<BrewInstalledInfo> {
    let json: serde_json::Value = serde_json::from_str(json_text).context("not valid JSON")?;

    let mut latest_versions = HashMap::new();
    let mut installed_versions = HashMap::new();

    for formula in json["formulae"].as_array().into_iter().flatten() {
        let Some(name) = formula["name"].as_str() else { continue };
        if let Some(latest) = formula["versions"]["stable"].as_str() {
            latest_versions.insert(name.to_string(), latest.to_string());
        }
        let newest_keg = formula["installed"].as_array().and_then(|kegs| kegs.last()).and_then(|keg| keg["version"].as_str());
        if let Some(version) = newest_keg {
            installed_versions.insert(name.to_string(), version.to_string());
        }
    }

    let mut cask_bundle_paths = HashSet::new();
    let mut root_required_casks = HashSet::new();
    let mut pkg_casks = HashMap::new();

    for cask in json["casks"].as_array().into_iter().flatten() {
        let mut declared_bundle_paths = Vec::new();
        let mut pkgutil_ids = Vec::new();

        for artifact in cask["artifacts"].as_array().into_iter().flatten() {
            // An `app` stanza is a bundle name installed to `target` when one is given and to
            // /Applications otherwise — Homebrew's own default for the `app` artifact.
            let target = artifact["target"].as_str().and_then(scanned_bundle_path);
            for name in strings_in(&artifact["app"]) {
                let default_path = format!("{APPLICATIONS_DIR}/{name}");
                declared_bundle_paths.extend(target.clone().or_else(|| scanned_bundle_path(&default_path)));
            }

            for entry in artifact["uninstall"].as_array().into_iter().flatten() {
                declared_bundle_paths.extend(strings_in(&entry["delete"]).into_iter().filter_map(scanned_bundle_path));
                pkgutil_ids.extend(strings_in(&entry["pkgutil"]).into_iter().map(str::to_string));
            }
        }

        cask_bundle_paths.extend(declared_bundle_paths.iter().cloned());

        let Some(token) = cask["token"].as_str() else { continue };
        let latest_version = cask["version"].as_str();
        if let Some(latest) = latest_version {
            latest_versions.insert(token.to_string(), latest.to_string());
        }
        if cask_requires_root(cask) {
            root_required_casks.insert(token.to_string());
        }
        if cask_installs_a_pkg(cask) {
            let pkgutil_ids = pkgutil_ids_for_installed_version(&pkgutil_ids, latest_version, cask["installed"].as_str());
            pkg_casks.insert(token.to_string(), PkgCask { declared_bundle_paths, pkgutil_ids });
        }
    }

    Ok(BrewInstalledInfo {
        latest_versions,
        installed_versions,
        cask_bundle_paths,
        root_required_casks,
        pkg_casks,
        casks_left: HashSet::new(),
    })
}

/// `ids`, plus each of them with the installed version's parts in place of the catalog's.
///
/// `brew info --json=v2 --installed` describes the cask as the tap defines it *today*, with
/// `installed` saying which version this Mac has — so a `pkgutil` stanza written as
/// `net.temurin.#{version.major}.jdk` arrives as `net.temurin.27.jdk` on a Mac whose receipt is
/// `net.temurin.26.jdk`, and asking `pkgutil` about the 27 finds nothing. The installed cask's
/// own definition would be authoritative, but Homebrew's `.metadata/<version>/…/Casks/<token>.json`
/// is an empty object for anything installed from its API, so there is nothing better to read.
/// The interpolations Homebrew actually offers are `version`, `version.major` and, for a
/// `<version>,<build>` string, `version.csv.first`; substituting each of those three, catalog for
/// installed, covers what real stanzas do. A substitution that lands on no receipt costs a
/// `pkgutil --pkgs` that answers nothing.
fn pkgutil_ids_for_installed_version(ids: &[String], latest_version: Option<&str>, installed_version: Option<&str>) -> Vec<String> {
    let mut out: Vec<String> = ids.to_vec();
    let (Some(latest), Some(installed)) = (latest_version, installed_version) else { return out };
    if latest == installed {
        return out;
    }

    let substitutions = [
        (latest, installed),
        (version_before_comma(latest), version_before_comma(installed)),
        (version_major(latest), version_major(installed)),
    ];
    for (from, to) in substitutions {
        if from.is_empty() || from == to {
            continue;
        }
        for id in ids {
            if id.contains(from) {
                let substituted = id.replace(from, to);
                if !out.contains(&substituted) {
                    out.push(substituted);
                }
            }
        }
    }
    out
}

/// Homebrew's `version.csv.first`: the part of a `<version>,<build>` string before the comma.
fn version_before_comma(version: &str) -> &str {
    version.split(',').next().unwrap_or(version)
}

/// Homebrew's `version.major`: what comes before the first `.` or `,`.
fn version_major(version: &str) -> &str {
    version.split(['.', ',']).next().unwrap_or(version)
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
    owner_command(run_as, brew)
}

/// `program`, run as `run_as` through `sudo -u` when one is given (this process is root and the
/// work belongs to Homebrew's owner) and directly otherwise. `brew` and the `rm` in [`forget_cask`]
/// both go through here, so the two never run with different privilege.
fn owner_command(run_as: Option<&str>, program: impl AsRef<std::ffi::OsStr>) -> Command {
    match run_as {
        Some(username) => {
            let mut cmd = Command::new("/usr/bin/sudo");
            cmd.args(["-u", username, "-H"]).arg(program);
            cmd
        }
        None => Command::new(program),
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

/// Lists installed formulae (`kind` = `--formula`) or casks (`--cask`). Each entry carries its
/// Homebrew name as its `application_identifier` — the server's `HomebrewUpgradeScript` reads the
/// name from `--appName` and ignores `--appId`, but the backend's `is_patchable` requires an
/// identifier before it will run any `Script` row, and a formula/cask has nothing more stable to
/// offer than the token `brew upgrade` takes. The exception is a cask in
/// `info.root_required_casks`, which is left without one on purpose — see [`cask_requires_root`] —
/// and a cask in `info.casks_left` is not listed at all, because it is no longer Homebrew's to
/// report: see [`pkg_casks_leaving_homebrew`].
fn list_brew_packages(brew: &Path, run_as: Option<&str>, kind: &str, info: &BrewInstalledInfo) -> Vec<InstalledApp> {
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

    parse_brew_list(&String::from_utf8_lossy(&output.stdout), info)
}

/// The pure half of [`list_brew_packages`].
fn parse_brew_list(listing: &str, info: &BrewInstalledInfo) -> Vec<InstalledApp> {
    listing
        .lines()
        .filter_map(|line| {
            // Each line is "<name> <version>" for casks, or
            // "<name> <version1> [<version2> ...]" for formulae when more
            // than one keg is kept side by side. That list is in directory
            // order, not version order, so the formula's newest keg comes from
            // `brew info` (see `BrewInstalledInfo::installed_versions`); the
            // last token is only the fallback when `brew info` failed.
            let mut tokens = line.split_whitespace();
            let name = tokens.next()?.to_string();
            if info.casks_left.contains(&name) {
                return None;
            }
            let listed_version = tokens.last()?.to_string();
            let version = info.installed_versions.get(&name).cloned().unwrap_or(listed_version);
            let available_version = info.latest_versions.get(&name).cloned();
            let application_identifier = if info.root_required_casks.contains(&name) {
                None
            } else {
                Some(name.clone())
            };
            Some(InstalledApp {
                name,
                version,
                package_manager: Some(HOMEBREW_NAME.to_string()),
                application_identifier,
                available_version,
                update_available: None,
            })
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Trimmed from real `brew info --json=v2 --installed` output on a Mac
    /// running the fleet's own agent. Keeps the shapes that matter: a formula
    /// (for `latest_versions`), an `app` cask, a `pkg` cask whose `pkg` stanza
    /// is a bare string, and a `pkg` cask whose `uninstall` stanza names a
    /// single path — which Homebrew writes as a bare string rather than a
    /// one-element array. The last three are the `pkg` casks that decide
    /// `pkg_casks_leaving_homebrew` each way: `displaylink` whose stanzas
    /// name a folder that no longer exists while its receipt names the real
    /// bundle, `adobe-acrobat-reader` whose declared bundle Adobe's own
    /// updater has since moved, and `temurin`, a JDK with no bundle at all.
    const BREW_INFO_JSON: &str = r#"{
      "formulae": [
        { "name": "jq", "versions": { "stable": "1.7.1" } },
        {
          "name": "openssl@3",
          "versions": { "stable": "3.6.4" },
          "linked_keg": "3.6.4",
          "installed": [ { "version": "3.6.3" }, { "version": "3.6.4" } ]
        }
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
                  "launchctl": "com.microsoft.teams.TeamsUpdaterDaemon",
                  "pkgutil": [ "com.microsoft.MSTeamsAudioDevice", "com.microsoft.teams2" ],
                  "delete": [
                    "/Applications/Microsoft Teams.app",
                    "/Library/Preferences/com.microsoft.teams.plist"
                  ]
                }
              ]
            },
            { "pkg": "MicrosoftTeams.pkg" }
          ]
        },
        {
          "token": "displaylink",
          "version": "17.0,2026-09",
          "artifacts": [
            {
              "uninstall": [
                {
                  "launchctl": [ "com.displaylink.displaylinkmanager", "com.displaylink.useragent" ],
                  "quit": "DisplayLinkUserAgent",
                  "pkgutil": "com.displaylink.*",
                  "delete": [ "/Applications/DisplayLink", "/Library/LaunchDaemons/com.displaylink.displaylinkmanager.plist" ]
                }
              ]
            },
            { "pkg": [ "DisplayLink Manager Graphics Connectivity17.0-EXE.pkg" ] }
          ]
        },
        {
          "token": "adobe-acrobat-reader",
          "version": "26.001.21662",
          "artifacts": [
            {
              "uninstall": [
                {
                  "pkgutil": [ "com.adobe.acrobat.DC.reader.*", "com.adobe.armdc.app.pkg" ],
                  "delete": [ "/Applications/Adobe Acrobat Reader.app", "/Library/Preferences/com.adobe.reader.DC.WebResource.plist" ]
                }
              ]
            },
            { "pkg": [ "AcroRdrDC_2600121662_MUI.pkg" ] }
          ]
        },
        {
          "token": "temurin",
          "version": "27,35",
          "installed": "26.0.2.1,1",
          "artifacts": [
            { "uninstall": [ { "pkgutil": "net.temurin.27.jdk" } ] },
            { "pkg": [ "OpenJDK27U-jdk_aarch64_mac_hotspot_27_35.pkg" ] }
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
    fn parse_brew_installed_info_flags_casks_whose_upgrade_needs_root() {
        let info = parse_brew_installed_info(BREW_INFO_JSON).expect("should parse");

        // Every `pkg` cask — `microsoft-teams` by its `pkg` artifact (a bare string, so this is
        // also the single-item shape), the rest by that and their `pkgutil` uninstall. An `app`
        // cask installs and uninstalls entirely as the user and stays patchable.
        assert_eq!(
            info.root_required_casks,
            ["adobe-acrobat-reader", "displaylink", "microsoft-teams", "nextcloud", "temurin"]
                .into_iter()
                .map(str::to_string)
                .collect::<HashSet<String>>()
        );
    }

    #[test]
    fn cask_installs_a_pkg_is_the_installer_half_of_needing_root() {
        let installs_a_pkg = |json: &str| cask_installs_a_pkg(&serde_json::from_str::<serde_json::Value>(json).unwrap());

        assert!(installs_a_pkg(r#"{ "artifacts": [ { "pkg": "Thing.pkg" } ] }"#));
        assert!(installs_a_pkg(r#"{ "artifacts": [ { "installer": [ { "script": { "executable": "install.sh", "sudo": true } } ] } ] }"#));
        // Root for the uninstall alone is `cask_requires_root` but not this: the files are the
        // user's, and such a cask stays in Homebrew.
        assert!(!installs_a_pkg(r#"{ "artifacts": [ { "uninstall": [ { "launchctl": "com.example.daemon" } ] }, { "app": "App.app" } ] }"#));
        assert!(!installs_a_pkg(r#"{ "artifacts": [ { "app": "App.app" } ] }"#));
    }

    #[test]
    fn parse_brew_installed_info_collects_each_pkg_cask_with_its_declared_bundles_and_receipts() {
        let info = parse_brew_installed_info(BREW_INFO_JSON).expect("should parse");

        assert_eq!(info.pkg_casks.len(), 5, "{:?}", info.pkg_casks.keys().collect::<Vec<_>>());
        assert!(!info.pkg_casks.contains_key("rectangle"));
        // Both stanzas in both shapes: a bare-string `pkgutil` and a bare-string `delete` on
        // nextcloud, arrays on microsoft-teams.
        assert_eq!(
            info.pkg_casks["nextcloud"],
            PkgCask {
                declared_bundle_paths: vec![PathBuf::from("/Applications/Nextcloud.app")],
                pkgutil_ids: vec!["com.nextcloud.desktopclient".to_string()],
            }
        );
        assert_eq!(
            info.pkg_casks["microsoft-teams"],
            PkgCask {
                declared_bundle_paths: vec![PathBuf::from("/Applications/Microsoft Teams.app")],
                pkgutil_ids: vec!["com.microsoft.MSTeamsAudioDevice".to_string(), "com.microsoft.teams2".to_string()],
            }
        );
        // `/Applications/DisplayLink` is a folder, not a bundle: nothing declared, only a receipt.
        assert_eq!(
            info.pkg_casks["displaylink"],
            PkgCask { declared_bundle_paths: Vec::new(), pkgutil_ids: vec!["com.displaylink.*".to_string()] }
        );
        // The catalog's cask is 27 and the Mac has 26 installed, so the stanza's receipt id is
        // kept and the installed version's spellings are added beside it.
        let temurin = &info.pkg_casks["temurin"];
        assert!(temurin.declared_bundle_paths.is_empty());
        assert!(temurin.pkgutil_ids.contains(&"net.temurin.27.jdk".to_string()));
        assert!(temurin.pkgutil_ids.contains(&"net.temurin.26.jdk".to_string()), "{:?}", temurin.pkgutil_ids);
    }

    #[test]
    fn pkgutil_ids_follow_the_installed_version_not_the_catalogs() {
        let ids = |ids: &[&str], latest: Option<&str>, installed: Option<&str>| {
            pkgutil_ids_for_installed_version(&ids.iter().map(|id| id.to_string()).collect::<Vec<_>>(), latest, installed)
        };

        // `version.major` — Homebrew's `net.temurin.#{version.major}.jdk`. "27" is also the
        // catalog's `version.csv.first`, and the JSON cannot say which the author meant, so both
        // spellings are tried; the one that names no receipt costs a `pkgutil --pkgs` that
        // answers nothing.
        assert_eq!(
            ids(&["net.temurin.27.jdk"], Some("27,35"), Some("26.0.2.1,1")),
            vec!["net.temurin.27.jdk", "net.temurin.26.0.2.1.jdk", "net.temurin.26.jdk"]
        );
        // The whole version, and `version.csv.first`.
        assert_eq!(
            ids(&["com.example.pkg.2.1.0"], Some("2.1.0,900"), Some("2.0.5,850")),
            vec!["com.example.pkg.2.1.0", "com.example.pkg.2.0.5"]
        );
        // Nothing to do: same version, an id that does not spell the version, or no `installed`.
        assert_eq!(ids(&["com.nextcloud.desktopclient"], Some("34.0.4"), Some("34.0.1")), vec!["com.nextcloud.desktopclient"]);
        assert_eq!(ids(&["net.temurin.27.jdk"], Some("27,35"), Some("27,35")), vec!["net.temurin.27.jdk"]);
        assert_eq!(ids(&["net.temurin.27.jdk"], Some("27,35"), None), vec!["net.temurin.27.jdk"]);
    }

    /// The disk and the receipt database of the Mac the fixture was captured on, as
    /// `pkg_casks_leaving_homebrew`'s two probes would see them.
    fn on_disk(path: &Path) -> bool {
        [
            "/Applications/Nextcloud.app",
            "/Applications/Microsoft Teams.app",
            "/Applications/DisplayLink Manager.app",
            "/Applications/Rectangle.app",
            // Where Adobe's updater put Reader — under a new name, which no cask stanza or receipt
            // knows about.
            "/Applications/Adobe Acrobat DC/Adobe Acrobat.app",
            "/Library/Java/JavaVirtualMachines/temurin-26.jdk",
        ]
        .iter()
        .any(|known| Path::new(known) == path)
    }

    fn receipts(pkgutil_id: &str) -> Vec<PathBuf> {
        let paths: &[&str] = match pkgutil_id {
            "com.nextcloud.desktopclient" => &["/Applications/Nextcloud.app"],
            "com.microsoft.teams2" => &["/Applications/Microsoft Teams.app"],
            "com.displaylink.*" => &["/Applications/DisplayLink Manager.app"],
            // Adobe's receipt still names the bundle the installer wrote; the disk no longer has it.
            "com.adobe.acrobat.DC.reader.*" => &["/Applications/Adobe Acrobat Reader.app"],
            // The receipt the *installed* Temurin left; the catalog's 27 has none.
            "net.temurin.26.jdk" => &["/Library/Java/JavaVirtualMachines/temurin-26.jdk"],
            _ => &[],
        };
        paths.iter().map(PathBuf::from).collect()
    }

    fn paths(paths: &[&str]) -> Vec<PathBuf> {
        paths.iter().map(PathBuf::from).collect()
    }

    #[test]
    fn every_pkg_cask_with_a_known_bundle_leaves_homebrew_and_says_what_it_leaves_behind() {
        let info = parse_brew_installed_info(BREW_INFO_JSON).expect("should parse");

        let leaving = pkg_casks_leaving_homebrew(&info, &receipts, &on_disk);

        let leaving_cask = |token: &str, on_disk: &[&str], missing: &[&str]| LeavingCask {
            token: token.to_string(),
            bundles_on_disk: paths(on_disk),
            bundles_missing: paths(missing),
        };
        assert_eq!(
            leaving,
            vec![
                // The ghost: both its stanza and its receipt name a bundle that is gone (Adobe's
                // updater moved it). It leaves with nothing to hand to the scan — the scan already
                // reports what is really there — and Homebrew stops describing an install that isn't.
                leaving_cask("adobe-acrobat-reader", &[], &["/Applications/Adobe Acrobat Reader.app"]),
                // Receipt evidence alone: the cask's own stanzas name a folder that is not there.
                leaving_cask("displaylink", &["/Applications/DisplayLink Manager.app"], &[]),
                // Declared and receipted, once — the two sources agree and are not double-counted.
                leaving_cask("microsoft-teams", &["/Applications/Microsoft Teams.app"], &[]),
                leaving_cask("nextcloud", &["/Applications/Nextcloud.app"], &[]),
                // A JDK: found only through the receipt spelled with the installed major.
                leaving_cask("temurin", &["/Library/Java/JavaVirtualMachines/temurin-26.jdk"], &[]),
            ]
        );
    }

    #[test]
    fn a_pkg_cask_that_names_no_bundle_anywhere_stays_in_homebrew() {
        let json = r#"{ "casks": [ {
            "token": "some-driver",
            "version": "3.0",
            "installed": "3.0",
            "artifacts": [
              { "uninstall": [ { "pkgutil": "com.example.driver", "kext": "com.example.driver" } ] },
              { "pkg": "Driver.pkg" }
            ]
        } ] }"#;
        let info = parse_brew_installed_info(json).expect("should parse");

        // Its receipt lists only a kernel extension under /Library/Extensions — nowhere the scan
        // looks — so there is no bundle to report standalone and no evidence the record is a ghost.
        // It stays exactly as before: a Homebrew row without an identifier.
        assert!(pkg_casks_leaving_homebrew(&info, &|_| Vec::new(), &on_disk).is_empty());
        assert!(info.root_required_casks.contains("some-driver"));
    }

    #[test]
    fn a_cask_that_only_needs_root_to_uninstall_never_leaves_homebrew() {
        let json = r#"{ "casks": [ {
            "token": "some-agent",
            "version": "1.0",
            "artifacts": [
              { "uninstall": [ { "launchctl": "com.example.agent", "delete": "/Applications/Some Agent.app" } ] },
              { "app": [ "Some Agent.app" ] }
            ]
        } ] }"#;
        let info = parse_brew_installed_info(json).expect("should parse");

        // Root-required (Homebrew may reach for sudo on the launchctl plist), so it is reported
        // without an identifier — but its files are the user's, and it is not a pkg cask.
        assert!(info.root_required_casks.contains("some-agent"));
        assert!(pkg_casks_leaving_homebrew(&info, &receipts, &|_| true).is_empty());
    }

    #[test]
    fn leaving_homebrew_frees_the_bundle_for_the_scan_and_drops_the_homebrew_row() {
        let mut info = parse_brew_installed_info(BREW_INFO_JSON).expect("should parse");
        let nextcloud = PathBuf::from("/Applications/Nextcloud.app");
        assert!(info.cask_bundle_paths.contains(&nextcloud));

        info.leave_homebrew("nextcloud", &[nextcloud.clone()]);

        // The scan now reports /Applications/Nextcloud.app, with its bundle identifier.
        assert!(!info.cask_bundle_paths.contains(&nextcloud));
        // ...and `brew list` no longer contributes a row for it, whether or not the Caskroom entry
        // was actually removed — `brew list` may still print it if `rm` failed.
        let apps = parse_brew_list("rectangle 1.100\nnextcloud 34.0.1\nmicrosoft-teams 26.1.0\n", &info);
        let names: Vec<&str> = apps.iter().map(|app| app.name.as_str()).collect();
        assert_eq!(names, vec!["rectangle", "microsoft-teams"]);
        // A pkg cask that did *not* leave keeps today's shape: listed, unpatchable.
        assert_eq!(apps[1].application_identifier, None);
    }

    #[test]
    fn bundle_paths_in_receipt_reads_both_shapes_pkgutil_writes() {
        // A package installed into /Applications lists files relative to that location...
        assert_eq!(
            bundle_paths_in_receipt("Applications", "Microsoft Teams.app\nMicrosoft Teams.app/Contents\nMicrosoft Teams.app/Contents/Info.plist\n"),
            paths(&["/Applications/Microsoft Teams.app"])
        );
        // ...while one installed at the volume root carries the folder itself — which is also how
        // a JDK's receipt reads.
        assert_eq!(
            bundle_paths_in_receipt("", "Applications\nApplications/Nextcloud.app\nApplications/Nextcloud.app/Contents\n"),
            paths(&["/Applications/Nextcloud.app"])
        );
        assert_eq!(
            bundle_paths_in_receipt("", "Library\nLibrary/Java\nLibrary/Java/JavaVirtualMachines\nLibrary/Java/JavaVirtualMachines/temurin-26.jdk\nLibrary/Java/JavaVirtualMachines/temurin-26.jdk/Contents\n"),
            paths(&["/Library/Java/JavaVirtualMachines/temurin-26.jdk"])
        );
        // A receipt for something that is not a bundle the scan looks at contributes nothing.
        assert!(bundle_paths_in_receipt("Library/Audio/Plug-Ins/HAL", "MSTeamsAudioDevice.driver\n").is_empty());
        assert!(bundle_paths_in_receipt("Library/Extensions", "Driver.kext\n").is_empty());
    }

    #[test]
    fn receipt_location_reads_the_location_line_and_defaults_to_the_volume_root() {
        assert_eq!(receipt_location("package-id: com.microsoft.teams2\nversion: 26198\nvolume: /\nlocation: Applications\n"), "Applications");
        assert_eq!(receipt_location("package-id: com.nextcloud.desktopclient\nvolume: /\nlocation: \n"), "");
        assert_eq!(receipt_location("package-id: x\nlocation: /Applications/\n"), "Applications");
        assert_eq!(receipt_location("package-id: x\n"), "");
    }

    #[test]
    fn cask_requires_root_reads_every_root_reaching_stanza() {
        let needs_root = |json: &str| cask_requires_root(&serde_json::from_str::<serde_json::Value>(json).unwrap());

        assert!(needs_root(r#"{ "artifacts": [ { "installer": [ { "script": { "executable": "install.sh", "sudo": true } } ] } ] }"#));
        assert!(needs_root(r#"{ "artifacts": [ { "uninstall": [ { "kext": "com.example.driver" } ] } ] }"#));
        assert!(needs_root(r#"{ "artifacts": [ { "uninstall": [ { "script": { "executable": "uninstall.sh" } } ] } ] }"#));
        assert!(needs_root(r#"{ "artifacts": [ { "uninstall": [ { "launchctl": "com.example.daemon" } ] } ] }"#));
        // `quit`, `login_item` and `delete` of an /Applications bundle are all done as the user.
        assert!(!needs_root(
            r#"{ "artifacts": [ { "uninstall": [ { "quit": "com.example.App", "login_item": "App", "delete": "/Applications/App.app" } ] }, { "app": "App.app" } ] }"#
        ));
        assert!(!needs_root(r#"{ "artifacts": [] }"#));
        assert!(!needs_root(r#"{}"#));
    }

    #[test]
    fn parse_brew_list_reports_the_token_as_the_identifier_unless_root_is_needed() {
        let info = parse_brew_installed_info(BREW_INFO_JSON).expect("should parse");

        let apps = parse_brew_list("rectangle 1.100\nnextcloud 34.0.2\n", &info);

        assert_eq!(apps.len(), 2);
        assert_eq!(apps[0].name, "rectangle");
        assert_eq!(apps[0].application_identifier.as_deref(), Some("rectangle"));
        assert_eq!(apps[0].available_version.as_deref(), Some("1.100"));
        assert_eq!(apps[0].package_manager.as_deref(), Some(HOMEBREW_NAME));
        // The one row that must *not* look patchable: `is_patchable` requires an identifier, and
        // the per-user process cannot upgrade a `pkg` cask — see `cask_requires_root`.
        assert_eq!(apps[1].name, "nextcloud");
        assert_eq!(apps[1].application_identifier, None);
        assert_eq!(apps[1].available_version.as_deref(), Some("34.0.3"));
    }

    #[test]
    fn parse_brew_list_takes_the_newest_of_several_formula_versions() {
        let info = parse_brew_installed_info(BREW_INFO_JSON).expect("should parse");

        // The exact line `brew list --formula --versions` printed on a Mac with both kegs kept:
        // directory order, newest first. Taking the last token reported 3.6.3, so the server
        // never saw the upgrade land and re-ran the script every cycle.
        let apps = parse_brew_list("openssl@3 3.6.4 3.6.3\n", &info);

        assert_eq!(apps[0].version, "3.6.4");
        assert_eq!(apps[0].application_identifier.as_deref(), Some("openssl@3"));
    }

    #[test]
    fn parse_brew_list_falls_back_to_the_last_listed_version_without_brew_info() {
        // `brew info` is best-effort; a formula it did not describe keeps the old reading.
        let info = parse_brew_installed_info(BREW_INFO_JSON).expect("should parse");

        let apps = parse_brew_list("zstd 1.5.7 1.5.7_1\n", &info);

        assert_eq!(apps[0].version, "1.5.7_1");
    }

    #[test]
    fn parse_brew_installed_info_takes_bundle_names_from_the_app_stanza() {
        let info = parse_brew_installed_info(BREW_INFO_JSON).expect("should parse");

        assert!(info.cask_bundle_paths.contains(Path::new("/Applications/Rectangle.app")));
    }

    #[test]
    fn parse_brew_installed_info_takes_bundle_names_from_a_multi_path_uninstall_delete() {
        let info = parse_brew_installed_info(BREW_INFO_JSON).expect("should parse");

        // A `pkg` cask has no `app` stanza at all, so its `uninstall`'s
        // `delete` paths are the only place the bundle name appears.
        assert!(info.cask_bundle_paths.contains(Path::new("/Applications/Microsoft Teams.app")));
    }

    #[test]
    fn parse_brew_installed_info_takes_bundle_names_from_a_single_path_uninstall_delete() {
        let info = parse_brew_installed_info(BREW_INFO_JSON).expect("should parse");

        // The regression this test exists for: `nextcloud` names one path, so
        // Homebrew writes `delete` as a bare string. Reading only the array
        // form dropped it, `/Applications/Nextcloud.app` escaped the dedup in
        // `scan_installed_bundles`, and it was reported a second time as a
        // standalone application carrying a bundle identifier — which is what
        // made an unpatchable Homebrew row look patchable. See `strings_in`.
        assert!(
            info.cask_bundle_paths.contains(Path::new("/Applications/Nextcloud.app")),
            "single-path `delete` was dropped: {:?}",
            info.cask_bundle_paths
        );
    }

    #[test]
    fn parse_brew_installed_info_ignores_paths_outside_applications() {
        let info = parse_brew_installed_info(BREW_INFO_JSON).expect("should parse");

        // `/Library/Preferences/com.microsoft.teams.plist` sits beside a real
        // bundle path in the same `delete` array and must not be mistaken for
        // one — nor must the `pkg`/`binary` stanzas contribute anything.
        assert_eq!(
            info.cask_bundle_paths,
            paths(&[
                "/Applications/Rectangle.app",
                "/Applications/Microsoft Teams.app",
                "/Applications/Nextcloud.app",
                "/Applications/Adobe Acrobat Reader.app",
            ])
            .into_iter()
            .collect::<HashSet<PathBuf>>()
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
    fn scanned_bundle_path_accepts_exactly_the_places_the_scan_looks() {
        let accepted = |path: &str| scanned_bundle_path(path).is_some();

        assert!(accepted("/Applications/Nextcloud.app"));
        // One folder deep — where Adobe's updater put Reader — but no deeper.
        assert!(accepted("/Applications/Adobe Acrobat DC/Adobe Acrobat.app"));
        assert!(accepted("/Applications/Adobe Acrobat 2.0/Reader.app"), "a folder name with a dot in it is still a folder");
        assert!(!accepted("/Applications/Vendor/Suite/Deep.app"));
        // A bundle's own Contents/ is one folder deep too, and is not a place to find applications.
        assert!(!accepted("/Applications/Nextcloud.app/Helper.app"));
        assert!(!accepted("/Applications/Nextcloud.app/Contents/MacOS/nextcloudcmd"));
        // JDKs, in their one place; other bundle kinds nowhere.
        assert!(accepted("/Library/Java/JavaVirtualMachines/temurin-26.jdk"));
        assert!(!accepted("/Library/Java/JavaVirtualMachines/temurin-26.jdk/Contents/Home"));
        assert!(!accepted("/Applications/temurin-26.jdk"));
        assert!(!accepted("/Library/Extensions/Driver.kext"));
        assert!(!accepted("/Applications/DisplayLink"));
        assert!(!accepted("/Library/Preferences/com.example.plist"));
    }

    #[test]
    fn an_app_stanza_is_placed_at_its_target_or_under_applications() {
        let json = r#"{ "casks": [
            { "token": "plain", "version": "1", "artifacts": [ { "app": "Plain.app" } ] },
            { "token": "targeted", "version": "1", "artifacts": [ { "app": [ "Tool.app" ], "target": "/Applications/Vendor/Tool.app" } ] }
        ] }"#;
        let info = parse_brew_installed_info(json).expect("should parse");

        assert_eq!(
            info.cask_bundle_paths,
            paths(&["/Applications/Plain.app", "/Applications/Vendor/Tool.app"]).into_iter().collect::<HashSet<PathBuf>>()
        );
    }

    /// Writes a minimal `.app` bundle under the system temp directory: an XML Info.plist (which
    /// `plutil` converts like any real one) and, when asked, an App Store receipt in the place the
    /// store puts it. The receipt's bytes are irrelevant — only its presence is read.
    fn scratch_bundle(name: &str, bundle_id: &str, with_receipt: bool) -> PathBuf {
        let app = std::env::temp_dir().join(format!("kintsugi-system-info-test-{}-{name}.app", std::process::id()));
        scratch_bundle_at(&app, name, bundle_id, with_receipt)
    }

    /// The same minimal bundle, written at exactly `app` — for a `.jdk`, or one placed inside a
    /// scratch tree that `scan_applications_tree` is then pointed at.
    fn scratch_bundle_at(app: &Path, name: &str, bundle_id: &str, with_receipt: bool) -> PathBuf {
        let app = app.to_path_buf();
        let _ = fs::remove_dir_all(&app);
        fs::create_dir_all(app.join("Contents")).unwrap();
        fs::write(
            app.join("Contents/Info.plist"),
            format!(
                r#"<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleIdentifier</key><string>{bundle_id}</string>
  <key>CFBundleName</key><string>{name}</string>
  <key>CFBundleShortVersionString</key><string>1.2.3</string>
</dict></plist>
"#
            ),
        )
        .unwrap();
        if with_receipt {
            let receipt = app.join(APP_STORE_RECEIPT);
            fs::create_dir_all(receipt.parent().unwrap()).unwrap();
            fs::write(receipt, b"not a real receipt").unwrap();
        }
        app
    }

    #[test]
    fn read_app_bundle_reports_a_receipt_bearing_bundle_as_app_store_managed() {
        let app = scratch_bundle("wireguard", "com.wireguard.macos", true);

        let installed = read_app_bundle(&app).unwrap().expect("an App Store bundle is reported");

        assert_eq!(installed.package_manager.as_deref(), Some(APP_STORE_NAME));
        // The bundle identifier, not the ADAM ID: it is what the server's `lookup?bundleId=` and
        // `mas` both accept, and it needs no Spotlight index.
        assert_eq!(installed.application_identifier.as_deref(), Some("com.wireguard.macos"));
        assert_eq!(installed.version, "1.2.3");

        let _ = fs::remove_dir_all(app);
    }

    #[test]
    fn read_app_bundle_names_a_jdk_by_its_directory_not_its_plist() {
        // Temurin 26's real Info.plist: the version is in CFBundleName and the identifier is the
        // one every OpenJDK build shares. Neither can name a row that tracks "the latest 26".
        let jdk = std::env::temp_dir().join(format!("kintsugi-system-info-test-{}/temurin-26.jdk", std::process::id()));
        scratch_bundle_at(&jdk, "OpenJDK 26.0.2.1", "net.java.openjdk.jdk", false);

        let installed = read_app_bundle(&jdk).unwrap().expect("a JDK is reported");

        assert_eq!(installed.name, "temurin-26");
        assert_eq!(installed.version, "1.2.3");
        assert_eq!(installed.application_identifier.as_deref(), Some("net.java.openjdk.jdk"));
        assert_eq!(installed.package_manager, None);

        let _ = fs::remove_dir_all(jdk.parent().unwrap());
    }

    #[test]
    fn the_applications_scan_goes_one_folder_deep_and_no_further() {
        let root = std::env::temp_dir().join(format!("kintsugi-system-info-test-{}-applications", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        scratch_bundle_at(&root.join("Top.app"), "Top", "com.example.Top", false);
        scratch_bundle_at(&root.join("Adobe Acrobat DC/Adobe Acrobat.app"), "Acrobat", "com.adobe.Acrobat.Pro", false);
        scratch_bundle_at(&root.join("Vendor/Suite/Deep.app"), "Deep", "com.example.Deep", false);
        // A cask accounts for this one; a helper bundle inside another bundle is not an application.
        scratch_bundle_at(&root.join("Cask.app"), "Cask", "com.example.Cask", false);
        scratch_bundle_at(&root.join("Top.app/Contents/Helper.app"), "Helper", "com.example.Helper", false);

        let cask_bundle_paths: HashSet<PathBuf> = [root.join("Cask.app")].into_iter().collect();
        let mut names: Vec<String> = scan_applications_tree(&root, &cask_bundle_paths).into_iter().map(|app| app.name).collect();
        names.sort();

        assert_eq!(names, vec!["Acrobat", "Top"]);

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn read_app_bundle_keeps_a_standalone_bundle_unmanaged() {
        let app = scratch_bundle("standalone", "com.example.Standalone", false);

        let installed = read_app_bundle(&app).unwrap().expect("a standalone bundle is reported");

        assert_eq!(installed.package_manager, None);
        assert_eq!(installed.application_identifier.as_deref(), Some("com.example.Standalone"));

        let _ = fs::remove_dir_all(app);
    }

    #[test]
    fn read_app_bundle_tells_apple_app_store_apps_apart_from_the_os_by_the_receipt() {
        // Xcode, Pages and friends are `com.apple.*` and were skipped as "part of the OS" — while
        // four of them sat out of date on the fleet's own Mac. The receipt is what separates an
        // Apple bundle sold through the store from one that ships with macOS.
        let os_bundle = scratch_bundle("safari", "com.apple.Safari", false);
        let store_bundle = scratch_bundle("xcode", "com.apple.dt.Xcode", true);

        assert!(read_app_bundle(&os_bundle).unwrap().is_none());
        let xcode = read_app_bundle(&store_bundle).unwrap().expect("an Apple App Store app is reported");
        assert_eq!(xcode.package_manager.as_deref(), Some(APP_STORE_NAME));
        assert_eq!(xcode.application_identifier.as_deref(), Some("com.apple.dt.Xcode"));

        let _ = fs::remove_dir_all(os_bundle);
        let _ = fs::remove_dir_all(store_bundle);
    }

    #[test]
    fn parse_mdls_bool_reads_the_three_answers_mdls_gives() {
        assert_eq!(parse_mdls_bool("1\n"), Some(true));
        assert_eq!(parse_mdls_bool("0"), Some(false));
        // Unindexed bundle or absent attribute: not a "yes", so the row stays patchable.
        assert_eq!(parse_mdls_bool("(null)"), None);
        assert_eq!(parse_mdls_bool(""), None);
    }
}

