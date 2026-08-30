use std::collections::{HashMap, HashSet};
use std::net::UdpSocket;
use std::process::Command;

use anyhow::{Context, Result};
use serde::Serialize;
use winreg::enums::{HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, KEY_READ, KEY_WOW64_32KEY, KEY_WOW64_64KEY};
use winreg::RegKey;

#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize)]
pub struct InstalledApp {
    pub name: String,
    pub version: String,
    /// Name of another reported app that manages this one (e.g. "winget" for a winget package).
    /// Must match that app's own `name` exactly — the backend links a child to its manager by
    /// matching this against another entry's `name` in the same report.
    #[serde(rename = "packageManager", skip_serializing_if = "Option::is_none")]
    pub package_manager: Option<String>,
    /// Whatever stably names this application: the package id for a winget/Chocolatey entry, or
    /// the application's own key name under the uninstall registry for a plain install. Both the
    /// AI-authored scripts and the server-written package-manager ones receive this as `--appId`
    /// and address the application by it, so it must not be a display name.
    #[serde(rename = "applicationIdentifier", skip_serializing_if = "Option::is_none")]
    pub application_identifier: Option<String>,
    /// The latest version available, when known independently of any upgrade research — for
    /// package-manager entries, read straight from that manager's own catalog. Lets the backend
    /// tell whether an update is available without having to research it separately.
    #[serde(rename = "availableVersion", skip_serializing_if = "Option::is_none")]
    pub available_version: Option<String>,
}

/// Returns the machine's hostname.
pub fn hostname() -> Result<String> {
    gethostname::gethostname()
        .into_string()
        .map_err(|raw| anyhow::anyhow!("hostname is not valid UTF-8: {raw:?}"))
}

/// Values a PC's SMBIOS serial number field is routinely filled with by manufacturers who didn't
/// bother — a real and common Windows failure mode with no macOS equivalent, where the serial is
/// always present and always unique.
///
/// This matters far more than a cosmetic blank field: the serial number is this host's identity.
/// `CaService` stamps it into the client certificate's subject CN, and `[RequireAgentIdentity]`
/// compares that CN against the serial each request body claims. Two machines sharing a
/// placeholder serial would share one host record, one certificate, and each other's reported
/// inventory and patch results.
const PLACEHOLDER_SERIAL_NUMBERS: &[&str] = &[
    "to be filled by o.e.m.",
    "to be filled by o.e.m",
    "system serial number",
    "default string",
    "none",
    "n/a",
    "not applicable",
    "not specified",
    "0",
    "0123456789",
    "123456789",
    "invalid",
    "unknown",
];

fn is_usable_serial_number(candidate: &str) -> bool {
    let trimmed = candidate.trim();
    if trimmed.len() < 4 {
        return false;
    }
    let lowered = trimmed.to_lowercase();
    if PLACEHOLDER_SERIAL_NUMBERS.contains(&lowered.as_str()) {
        return false;
    }
    // A field padded out with a single repeated character ("0000000", "XXXXXXXX") is a placeholder
    // in every practical sense even when it isn't one of the exact strings above.
    !trimmed.chars().all(|c| c == trimmed.chars().next().unwrap_or(' '))
}

/// This host's stable unique identifier, tried in order of preference:
///
/// 1. the SMBIOS system serial number, read straight from the registry rather than via WMI —
///    matching what an administrator sees on the chassis sticker and in every asset system, and
///    the closest analogue to what the macOS agent reports;
/// 2. failing that (see `PLACEHOLDER_SERIAL_NUMBERS` — plenty of machines ship with the field
///    unset), the machine's cryptographic `MachineGuid`, which Windows generates per install and
///    is genuinely unique.
///
/// Returns an error rather than any fallback value if neither is usable. Enrolling under a
/// non-unique identifier is worse than not enrolling: it would silently merge this host's record
/// with another machine's.
pub fn serial_number() -> Result<String> {
    if let Some(serial) = smbios_serial_number() {
        return Ok(serial);
    }

    crate::logging::warn(
        "this machine's SMBIOS serial number is missing or a manufacturer placeholder; \
         falling back to the Windows MachineGuid as this host's identifier",
    );

    machine_guid().context(
        "could not determine a unique identifier for this machine — neither the SMBIOS serial \
         number nor the Windows MachineGuid is usable, and enrolling under a non-unique one would \
         merge this host's record with another machine's",
    )
}

fn smbios_serial_number() -> Option<String> {
    let value: String = RegKey::predef(HKEY_LOCAL_MACHINE)
        .open_subkey_with_flags(r"HARDWARE\DESCRIPTION\System\BIOS", KEY_READ)
        .ok()?
        .get_value("SystemSerialNumber")
        .ok()?;

    is_usable_serial_number(&value).then(|| value.trim().to_string())
}

fn machine_guid() -> Option<String> {
    // Under the 64-bit view explicitly: a 32-bit build of this agent would otherwise be redirected
    // to the WOW6432Node copy, which is a *different* GUID — so the same machine would enroll
    // twice if the agent's bitness ever changed.
    let value: String = RegKey::predef(HKEY_LOCAL_MACHINE)
        .open_subkey_with_flags(r"SOFTWARE\Microsoft\Cryptography", KEY_READ | KEY_WOW64_64KEY)
        .ok()?
        .get_value("MachineGuid")
        .ok()?;

    is_usable_serial_number(&value).then(|| value.trim().to_string())
}

/// Returns a human-readable OS name and version, e.g. "Windows 11 Pro 23H2 (22631)".
///
/// Read from the registry rather than from `GetVersionEx`/`RTL_OSVERSIONINFO`: those are subject
/// to application-compatibility shimming and famously report 6.2 for an unmanifested process,
/// whereas these values are what `winver` itself shows. The string only has to be recognizable to
/// a human and to contain "Windows" for `PlatformBucket.From` to bucket it — see
/// Kintsugi.Application/UpgradePaths/PlatformBucket.cs.
pub fn operating_system() -> Result<String> {
    let key = RegKey::predef(HKEY_LOCAL_MACHINE)
        .open_subkey_with_flags(r"SOFTWARE\Microsoft\Windows NT\CurrentVersion", KEY_READ | KEY_WOW64_64KEY)
        .context("could not open the Windows version registry key")?;

    let product_name: String = key.get_value("ProductName").context("no ProductName in the Windows version registry key")?;

    // DisplayVersion ("23H2") replaced ReleaseId ("2009") in Windows 10 20H2 onwards; older
    // installs only have the latter, and a very old one has neither.
    let display_version: Option<String> = key.get_value("DisplayVersion").ok().or_else(|| key.get_value("ReleaseId").ok());
    let build: Option<String> = key.get_value("CurrentBuildNumber").ok();

    // Windows 11 still reports "Windows 10 ..." as its ProductName; the build number is what
    // actually distinguishes them, so correct it rather than reporting something an administrator
    // would read as wrong.
    let build_number: Option<u32> = build.as_ref().and_then(|b| b.parse().ok());
    let product_name = match build_number {
        Some(number) if number >= 22000 && product_name.starts_with("Windows 10") => product_name.replacen("Windows 10", "Windows 11", 1),
        _ => product_name,
    };

    Ok(match (display_version, build) {
        (Some(version), Some(build)) => format!("{product_name} {version} ({build})"),
        (Some(version), None) => format!("{product_name} {version}"),
        (None, Some(build)) => format!("{product_name} ({build})"),
        (None, None) => product_name,
    })
}

/// Returns the local IP address this machine would use to route outbound traffic. Uses a UDP
/// "connect" (no packets are actually sent — it only resolves the local route) so it works the
/// same whether the target is reachable or not, and needs no extra permissions.
pub fn local_ip_address() -> Result<String> {
    let socket = UdpSocket::bind("0.0.0.0:0").context("failed to bind local UDP socket")?;
    socket.connect("8.8.8.8:80").context("failed to resolve local outbound route")?;
    let addr = socket.local_addr().context("failed to read local socket address")?;
    Ok(addr.ip().to_string())
}

const UNINSTALL_KEY: &str = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

/// Scans the Windows uninstall registry — the same source Settings > Apps and every other
/// inventory tool reads — for installed applications. The Windows counterpart to the macOS agent's
/// `/Applications` bundle scan.
///
/// Three separate views are read, because an application appears in exactly one of them and
/// missing a view means silently missing every application installed that way:
/// - HKLM under the 64-bit view: machine-wide 64-bit installs;
/// - HKLM under the 32-bit view (`WOW6432Node`): machine-wide 32-bit installs, still the majority
///   of desktop software;
/// - HKCU: per-user installs, which a standard user can perform without an administrator and which
///   therefore never appear in either HKLM view.
///
/// `managed_keys` names the applications a package manager has already reported (see
/// [`scan_package_managers`]); those are skipped, so a winget-installed application is reported
/// once — as winget-managed — rather than also as a separate unmanaged entry, which would give one
/// install two competing upgrade paths.
///
/// This mirrors what the macOS agent does with Homebrew casks and `/Applications`, but it cannot
/// mirror it *directly*, and that difference is the whole reason [`ManagedKeys`] exists: a cask's
/// artifact list literally names `/Applications/<Name>.app`, so both sides of that join are already
/// the same namespace. Here they are not — winget knows Firefox as `Mozilla.Firefox`, Chocolatey as
/// `firefox`, and the registry as `Mozilla Firefox 154.0.1 (x64 en-US)` — so the join has to be made
/// on every key each side can offer.
pub fn scan_installed_programs(managed_keys: &ManagedKeys) -> Vec<InstalledApp> {
    let mut apps = Vec::new();

    for (root, flags) in [
        (HKEY_LOCAL_MACHINE, KEY_READ | KEY_WOW64_64KEY),
        (HKEY_LOCAL_MACHINE, KEY_READ | KEY_WOW64_32KEY),
        (HKEY_CURRENT_USER, KEY_READ),
    ] {
        let key = match RegKey::predef(root).open_subkey_with_flags(UNINSTALL_KEY, flags) {
            Ok(key) => key,
            Err(err) => {
                crate::logging::warn(&format!("could not read an uninstall registry view: {err}"));
                continue;
            }
        };

        for subkey_name in key.enum_keys().flatten() {
            let Ok(subkey) = key.open_subkey_with_flags(&subkey_name, flags) else {
                continue;
            };

            if let Some(app) = read_uninstall_entry(&subkey, &subkey_name, managed_keys) {
                apps.push(app);
            }
        }
    }

    apps
}

/// Turns one uninstall-registry subkey into a reportable application, or `None` if it isn't one.
///
/// Most subkeys under this path are not applications a person installed: Windows updates, patch
/// entries, and every component an installer registered but hid. The filtering below is what the
/// `com.apple.` bundle-identifier check is on macOS — without it this reports several hundred
/// entries per host, most of them noise.
fn read_uninstall_entry(subkey: &RegKey, subkey_name: &str, managed_keys: &ManagedKeys) -> Option<InstalledApp> {
    let name: String = subkey.get_value("DisplayName").ok()?;
    let name = name.trim().to_string();
    if name.is_empty() {
        return None;
    }

    // SystemComponent=1 marks a component registered for uninstall bookkeeping but deliberately
    // hidden from Add/Remove Programs — a redistributable's internal entry, not an application.
    if subkey.get_value::<u32, _>("SystemComponent").unwrap_or(0) == 1 {
        return None;
    }

    // A ParentKeyName/ParentDisplayName marks a patch or language pack rolled up under another
    // entry; reporting it separately would show the same application several times at several
    // different versions.
    if subkey.get_value::<String, _>("ParentKeyName").is_ok() || subkey.get_value::<String, _>("ParentDisplayName").is_ok() {
        return None;
    }

    // ReleaseType names a Windows servicing entry ("Security Update", "Hotfix", "Update Rollup").
    // Those are the OS's business — reported through the OS-update path (see `os_update`), not as
    // patchable applications.
    if let Ok(release_type) = subkey.get_value::<String, _>("ReleaseType") {
        if !release_type.eq_ignore_ascii_case("Application") {
            return None;
        }
    }

    // The subkey name is the application's stable identifier — an MSI product code
    // ("{XXXXXXXX-...}") or the vendor's own key name. This is what the generated scripts receive
    // as --appId and look the application's registry entry back up by, so it is load-bearing: a
    // Script row with no identifier is never patchable at all (see `upgrade::is_patchable`).
    let application_identifier = subkey_name.trim().to_string();
    if application_identifier.is_empty() || managed_keys.claims(&application_identifier, &name) {
        return None;
    }

    let version: String = subkey
        .get_value("DisplayVersion")
        .ok()
        .map(|v: String| v.trim().to_string())
        .filter(|v| !v.is_empty())
        .unwrap_or_else(|| "unknown".to_string());

    Some(InstalledApp {
        name,
        version,
        package_manager: None,
        application_identifier: Some(application_identifier),
        available_version: None,
    })
}

/// Name reported for winget's own entry, and the `packageManager` value every package it manages
/// is tagged with. Must match `PackageManagerCatalog.Winget` on the backend exactly — that name is
/// what the upgrade path's platform bucket is keyed by (see `PlatformBucket.ForPackageManager`).
const WINGET_NAME: &str = "winget";

/// As above, for Chocolatey — matching `PackageManagerCatalog.Chocolatey`.
const CHOCOLATEY_NAME: &str = "Chocolatey";

/// Every string by which a package manager's packages might be recognized in the uninstall
/// registry — the join that keeps one installed application from being reported twice.
///
/// A set of plain identifiers isn't enough, because no single identifier is shared by both sides:
/// winget reports `Mozilla.Firefox`, Chocolatey reports `firefox`, and the registry knows the same
/// install as a subkey called `Mozilla Firefox 154.0.1 (x64 en-US)` with a `DisplayName` of
/// `Mozilla Firefox`. So every key a manager can offer goes in, and a registry entry is skipped if
/// *either* its subkey name or its display name matches one of them.
///
/// Everything is stored lowercased and compared lowercased: winget and the registry routinely
/// disagree on the casing of the same name, and a case-sensitive miss here is silent — it produces
/// a duplicate rather than an error.
#[derive(Debug, Default)]
pub struct ManagedKeys(HashSet<String>);

impl ManagedKeys {
    pub fn insert(&mut self, key: &str) {
        let key = key.trim();
        if !key.is_empty() {
            self.0.insert(key.to_lowercase());
        }
    }

    /// Whether a package manager already reports the application behind this registry entry.
    pub fn claims(&self, subkey_name: &str, display_name: &str) -> bool {
        self.0.contains(&subkey_name.trim().to_lowercase()) || self.0.contains(&display_name.trim().to_lowercase())
    }

    fn extend(&mut self, other: ManagedKeys) {
        self.0.extend(other.0);
    }

    #[cfg(test)]
    fn len(&self) -> usize {
        self.0.len()
    }
}

/// Strips the `ARP\Machine\X64\` (or `ARP\User\...`) prefix winget puts on the id of a package it
/// only knows about because it found it in the uninstall registry — i.e. one with no entry in any
/// configured source.
///
/// What's left is the registry subkey name itself, which is the strongest possible join key: an
/// exact match rather than a name comparison. Anything without that prefix is a real catalog id and
/// is returned unchanged.
fn strip_arp_prefix(winget_id: &str) -> &str {
    if winget_id.starts_with("ARP\\") {
        winget_id.rsplit('\\').next().unwrap_or(winget_id)
    } else {
        winget_id
    }
}

/// Result of [`scan_package_managers`]: the package-manager-managed apps to report, plus the keys
/// those packages already account for.
pub struct PackageManagerScan {
    pub apps: Vec<InstalledApp>,
    /// Passed to [`scan_installed_programs`] so it doesn't report the same application a second
    /// time as a separate, unmanaged entry — the counterpart to the macOS agent's
    /// `cask_app_bundle_names`.
    pub managed_keys: ManagedKeys,
}

/// Reports winget and Chocolatey themselves (with their own versions) plus every package each one
/// manages, tagged accordingly. Returns an empty scan for a manager that isn't installed — most
/// PCs have neither, some have one, a few have both.
pub fn scan_package_managers() -> PackageManagerScan {
    let mut apps = Vec::new();
    let mut managed_keys = ManagedKeys::default();

    let winget = scan_winget();
    apps.extend(winget.apps);
    managed_keys.extend(winget.managed_keys);

    let chocolatey = scan_chocolatey();
    apps.extend(chocolatey.apps);
    managed_keys.extend(chocolatey.managed_keys);

    PackageManagerScan { apps, managed_keys }
}

fn empty_scan() -> PackageManagerScan {
    PackageManagerScan { apps: Vec::new(), managed_keys: ManagedKeys::default() }
}

/// Runs a package manager's CLI, returning its stdout on success. Both managers are invoked
/// through their bare name rather than an absolute path: unlike Homebrew (which the macOS agent has
/// to locate by hand because a root LaunchDaemon gets a minimal PATH), both of these install
/// themselves onto the machine-wide PATH that a SYSTEM service inherits.
fn run_package_manager(program: &str, args: &[&str]) -> Option<String> {
    match Command::new(program).args(args).output() {
        Ok(output) if output.status.success() => Some(String::from_utf8_lossy(&output.stdout).to_string()),
        Ok(output) => {
            crate::logging::warn(&format!(
                "`{program} {}` exited with {}: {}",
                args.join(" "),
                output.status,
                String::from_utf8_lossy(&output.stderr).trim()
            ));
            None
        }
        // Not a warning: "the manager isn't installed" is the ordinary case on most PCs, and the
        // caller reports an empty scan for it.
        Err(_) => None,
    }
}

fn scan_winget() -> PackageManagerScan {
    let Some(version_output) = run_package_manager("winget", &["--version"]) else {
        return empty_scan();
    };

    let mut apps = vec![InstalledApp {
        name: WINGET_NAME.to_string(),
        // winget prints its version as "v1.9.25200"; the leading "v" is stripped so it compares
        // against what the server-written self-update script reports (which strips it too).
        version: version_output.trim().trim_start_matches('v').to_string(),
        package_manager: None,
        application_identifier: Some(WINGET_NAME.to_string()),
        available_version: None,
    }];

    let mut managed_keys = ManagedKeys::default();

    // --accept-source-agreements so a never-used winget doesn't block forever on a first-run
    // prompt; --disable-interactivity for the same reason. The listing is asked for in the fixed
    // "Name Id Version Available Source" column layout, which is the only machine-readable form
    // winget offers — it has no JSON output mode.
    if let Some(listing) = run_package_manager(
        "winget",
        &["list", "--accept-source-agreements", "--disable-interactivity"],
    ) {
        for package in parse_winget_list(&listing) {
            // Three keys per package, because which one matches depends on how the package got
            // there: the catalog id, the registry subkey name hidden inside an `ARP\...` id, and
            // the display name — which for a winget-listed package is read from the uninstall
            // registry's own DisplayName, making it the reliable join for a catalog package.
            managed_keys.insert(&package.id);
            managed_keys.insert(strip_arp_prefix(&package.id));
            managed_keys.insert(&package.name);

            apps.push(InstalledApp {
                name: package.name,
                version: package.version,
                package_manager: Some(WINGET_NAME.to_string()),
                application_identifier: Some(package.id),
                available_version: package.available,
            });
        }
    }

    PackageManagerScan { apps, managed_keys }
}

/// One row of `winget list`'s table output.
#[derive(Debug, PartialEq, Eq)]
struct WingetPackage {
    name: String,
    id: String,
    version: String,
    available: Option<String>,
}

/// Parses `winget list`'s fixed-width table.
///
/// Split out from the subprocess call so it can be exercised against real captured output rather
/// than only on a machine with winget installed. The table's columns are positional, not
/// delimited: a package's display name routinely contains spaces, so splitting on whitespace would
/// tear names apart. The header row's own column offsets are what defines the field boundaries,
/// which is why parsing starts by finding it.
fn parse_winget_list(output: &str) -> Vec<WingetPackage> {
    // Split on carriage returns as well as newlines. winget draws its "searching" spinner by
    // overwriting one line with bare `\r`s, so the spinner frames and the real header arrive as a
    // single `\n`-delimited line — which meant `str::lines()` never found a line *starting* with
    // "Name", and every package on the machine was silently dropped.
    let lines: Vec<&str> = output.split(['\r', '\n']).collect();

    // The header is the line starting with "Name" that also carries an "Id" column. Everything
    // before it is a progress spinner and first-run banner text. No header means winget printed
    // something other than a listing (an error, an empty result) — nothing to parse.
    let Some(header_index) = lines.iter().position(|line| line.trim_start().starts_with("Name") && line.contains("Id")) else {
        return Vec::new();
    };

    let header = lines[header_index];
    let (Some(id_start), Some(version_start)) = (header.find("Id"), header.find("Version")) else {
        return Vec::new();
    };
    let available_start = header.find("Available");
    // The listing's last column names the source a package came from ("winget", "msstore"). It has
    // to bound the Available column, or a package that *does* have an update reports its available
    // version with the source name glued onto the end.
    let source_start = header.find("Source");

    let mut packages = Vec::new();

    for line in lines.iter().skip(header_index + 1) {
        // The row of dashes separating the header from the data, and any trailing summary line.
        if line.trim().is_empty() || line.trim_start().starts_with('-') {
            continue;
        }

        let name = slice_columns(line, 0, id_start);
        let id = slice_columns(line, id_start, version_start);
        let version_end = available_start.or(source_start).unwrap_or(line.len());
        let version = slice_columns(line, version_start, version_end);

        if name.is_empty() || id.is_empty() || version.is_empty() {
            continue;
        }

        let available = available_start
            .map(|start| slice_columns(line, start, source_start.unwrap_or(line.len())))
            .filter(|value| !value.is_empty())
            // A real version always starts with a digit. Belt and braces alongside the Source
            // bound above: on a listing with no Source column at all, this is what keeps a stray
            // trailing token out of the reported available version.
            .filter(|value| value.starts_with(|c: char| c.is_ascii_digit()));

        packages.push(WingetPackage { name, id, version, available });
    }

    packages
}

/// Reads `line`'s bytes between two column offsets, tolerating a row shorter than the header (the
/// last columns are simply absent) and a multi-byte character straddling the boundary.
fn slice_columns(line: &str, start: usize, end: usize) -> String {
    let chars: Vec<char> = line.chars().collect();
    if start >= chars.len() {
        return String::new();
    }
    let end = end.min(chars.len());
    chars[start..end].iter().collect::<String>().trim().to_string()
}

fn scan_chocolatey() -> PackageManagerScan {
    let Some(version_output) = run_package_manager("choco", &["--version"]) else {
        return empty_scan();
    };

    let mut apps = vec![InstalledApp {
        name: CHOCOLATEY_NAME.to_string(),
        version: version_output.trim().to_string(),
        package_manager: None,
        application_identifier: Some("chocolatey".to_string()),
        available_version: None,
    }];

    let mut managed_keys = ManagedKeys::default();

    // --limit-output is Chocolatey's machine-readable mode: one "id|version" per line, no banner,
    // no summary. Unlike winget there's no column guessing to do.
    let installed = run_package_manager("choco", &["list", "--limit-output"])
        .map(|listing| parse_choco_list(&listing))
        .unwrap_or_default();

    // Asked for separately because `choco list` alone never reports what's available. Best-effort:
    // this reaches the network, so it's the piece most likely to fail, and a failure here just
    // means the backend learns the latest version from the upgrade script instead.
    let available: HashMap<String, String> = run_package_manager("choco", &["outdated", "--limit-output"])
        .map(|listing| parse_choco_outdated(&listing))
        .unwrap_or_default();

    for (id, version) in installed {
        // Chocolatey's own package is already reported above, as the manager itself.
        if id.eq_ignore_ascii_case("chocolatey") {
            continue;
        }

        // Chocolatey offers only its own package id — `choco list` has no display-name column, and
        // the id is often nothing like the name the underlying installer registers ("firefox" vs
        // "Mozilla Firefox"). So a Chocolatey package whose installer writes an unrelated
        // uninstall-registry entry is still reported twice: once here, once from the registry.
        // Closing that gap properly means reading the `.registry` snapshot Chocolatey writes under
        // `%ChocolateyInstall%\.chocolatey\<id>.<version>\`, which records the exact registry keys
        // that package created — worth doing if duplicates turn out to be common in practice.
        managed_keys.insert(&id);

        apps.push(InstalledApp {
            name: id.clone(),
            version,
            package_manager: Some(CHOCOLATEY_NAME.to_string()),
            available_version: available.get(&id).cloned(),
            application_identifier: Some(id),
        });
    }

    PackageManagerScan { apps, managed_keys }
}

/// Parses `choco list --limit-output`: one `id|version` per line.
fn parse_choco_list(output: &str) -> Vec<(String, String)> {
    output
        .lines()
        .filter_map(|line| {
            let (id, version) = line.trim().split_once('|')?;
            (!id.is_empty() && !version.is_empty()).then(|| (id.to_string(), version.to_string()))
        })
        .collect()
}

/// Parses `choco outdated --limit-output`: one
/// `id|current version|available version|is pinned` per line.
fn parse_choco_outdated(output: &str) -> HashMap<String, String> {
    output
        .lines()
        .filter_map(|line| {
            let mut fields = line.trim().split('|');
            let id = fields.next()?;
            let _current = fields.next()?;
            let available = fields.next()?;
            (!id.is_empty() && !available.is_empty()).then(|| (id.to_string(), available.to_string()))
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn is_usable_serial_number_accepts_a_real_serial() {
        assert!(is_usable_serial_number("5CD2145XYZ"));
        assert!(is_usable_serial_number("PF3K2N9B"));
    }

    #[test]
    fn is_usable_serial_number_rejects_manufacturer_placeholders() {
        // Every one of these has been observed shipped on real hardware. Accepting any of them
        // would give two machines the same identity — and so the same certificate CN, the same
        // host record, and each other's inventory.
        for placeholder in ["To Be Filled By O.E.M.", "System Serial Number", "Default string", "None", "0", "Unknown"] {
            assert!(!is_usable_serial_number(placeholder), "{placeholder} should be rejected");
        }
    }

    #[test]
    fn is_usable_serial_number_rejects_a_single_repeated_character() {
        assert!(!is_usable_serial_number("00000000"));
        assert!(!is_usable_serial_number("XXXXXXXXXXXX"));
    }

    #[test]
    fn is_usable_serial_number_rejects_something_too_short_to_be_real() {
        assert!(!is_usable_serial_number(""));
        assert!(!is_usable_serial_number("   "));
        assert!(!is_usable_serial_number("AB"));
    }

    #[test]
    fn is_usable_serial_number_ignores_surrounding_whitespace() {
        assert!(is_usable_serial_number("  5CD2145XYZ  "));
        assert!(!is_usable_serial_number("  None  "));
    }

    const WINGET_LIST_SAMPLE: &str = concat!(
        "   -\r   \\\r",
        "Name                           Id                            Version      Available    Source\n",
        "-----------------------------------------------------------------------------------------------\n",
        "Mozilla Firefox                Mozilla.Firefox               153.0        154.0.1      winget\n",
        "VLC media player               VideoLAN.VLC                  3.0.21       3.0.23       winget\n",
        "7-Zip 24.09 (x64)              7zip.7zip                     24.09                     winget\n",
        "Some Local Thing               ARP\\Machine\\X64\\SomeThing      1.0.0\n",
    );

    #[test]
    fn parse_winget_list_reads_names_containing_spaces_intact() {
        let packages = parse_winget_list(WINGET_LIST_SAMPLE);

        // Splitting on whitespace — the obvious first instinct — would turn this into "Mozilla".
        assert_eq!(packages[0].name, "Mozilla Firefox");
        assert_eq!(packages[0].id, "Mozilla.Firefox");
        assert_eq!(packages[0].version, "153.0");
    }

    #[test]
    fn parse_winget_list_reports_an_available_version_only_when_there_is_one() {
        let packages = parse_winget_list(WINGET_LIST_SAMPLE);

        assert_eq!(packages[0].available.as_deref(), Some("154.0.1"));
        // 7-Zip is current, so winget prints the source name in that column instead — which must
        // not be mistaken for a version, or the backend would think an upgrade to "winget" exists.
        let seven_zip = packages.iter().find(|p| p.id == "7zip.7zip").expect("7-Zip should be listed");
        assert_eq!(seven_zip.available, None);
    }

    #[test]
    fn parse_winget_list_skips_the_banner_and_separator_rows() {
        let packages = parse_winget_list(WINGET_LIST_SAMPLE);

        assert_eq!(packages.len(), 4);
        assert!(packages.iter().all(|p| !p.name.starts_with('-')));
    }

    #[test]
    fn parse_winget_list_handles_a_row_shorter_than_the_header() {
        let packages = parse_winget_list(WINGET_LIST_SAMPLE);

        // The last row has no Available or Source column at all — a naive fixed-offset slice would
        // panic on it.
        let local = packages.last().expect("the short row should still be parsed");
        assert_eq!(local.name, "Some Local Thing");
        assert_eq!(local.version, "1.0.0");
        assert_eq!(local.available, None);
    }

    #[test]
    fn parse_winget_list_returns_nothing_for_output_with_no_header() {
        assert!(parse_winget_list("winget: command failed\n").is_empty());
        assert!(parse_winget_list("").is_empty());
    }

    /// The keys a winget listing contributes, built exactly as `scan_winget` does. Kept here rather
    /// than reaching into that function so the join can be exercised without winget installed.
    fn managed_keys_from_winget(listing: &str) -> ManagedKeys {
        let mut keys = ManagedKeys::default();
        for package in parse_winget_list(listing) {
            keys.insert(&package.id);
            keys.insert(strip_arp_prefix(&package.id));
            keys.insert(&package.name);
        }
        keys
    }

    #[test]
    fn strip_arp_prefix_recovers_the_registry_subkey_name() {
        // winget gives a package it only found in the uninstall registry an id of this shape. The
        // tail is the registry subkey name itself, which is an exact join key rather than a name
        // comparison.
        assert_eq!(strip_arp_prefix(r"ARP\Machine\X64\{90160000-008C-0000-1000-0000000FF1CE}"), "{90160000-008C-0000-1000-0000000FF1CE}");
        assert_eq!(strip_arp_prefix(r"ARP\User\X64\Some Vendor App"), "Some Vendor App");
    }

    #[test]
    fn strip_arp_prefix_leaves_a_real_catalog_id_alone() {
        assert_eq!(strip_arp_prefix("Mozilla.Firefox"), "Mozilla.Firefox");
        assert_eq!(strip_arp_prefix("7zip.7zip"), "7zip.7zip");
    }

    #[test]
    fn a_winget_managed_application_is_claimed_by_its_registry_display_name() {
        // THE regression this join exists for. winget knows the package as "Mozilla.Firefox"; the
        // registry knows the same install as a subkey called "Mozilla Firefox 154.0.1 (x64 en-US)".
        // Matching identifier-to-identifier — which is what the macOS cask join does, because there
        // both sides *are* the same namespace — never matches here, so the application would be
        // reported twice: once winget-managed with a signed script, and once standalone with a
        // competing AI-researched one.
        let keys = managed_keys_from_winget(WINGET_LIST_SAMPLE);

        assert!(keys.claims(r"Mozilla Firefox 154.0.1 (x64 en-US)", "Mozilla Firefox"));
    }

    #[test]
    fn a_winget_managed_application_is_claimed_by_its_registry_subkey_when_winget_only_knows_it_from_arp() {
        let keys = managed_keys_from_winget(WINGET_LIST_SAMPLE);

        // The last row's id is ARP-derived; its tail is the subkey name, and the display name in
        // the registry need not match winget's Name column at all.
        assert!(keys.claims(r"SomeThing", "Something Entirely Different"));
    }

    #[test]
    fn the_join_is_case_insensitive() {
        // winget and the registry routinely disagree on casing for the same application, and a
        // case-sensitive miss is silent — it produces a duplicate, not an error.
        let keys = managed_keys_from_winget(WINGET_LIST_SAMPLE);

        assert!(keys.claims("vlc media player", "VLC MEDIA PLAYER"));
    }

    #[test]
    fn an_unmanaged_application_is_not_claimed() {
        // The other half of the property: the join must not be so loose that it swallows genuinely
        // unmanaged applications, which would drop them from the inventory entirely.
        let keys = managed_keys_from_winget(WINGET_LIST_SAMPLE);

        assert!(!keys.claims("{11111111-2222-3333-4444-555555555555}", "Some Unrelated Application"));
    }

    #[test]
    fn a_chocolatey_managed_application_is_claimed_by_its_package_id() {
        let mut keys = ManagedKeys::default();
        for (id, _) in parse_choco_list("firefox|154.0.1\n") {
            keys.insert(&id);
        }

        assert!(keys.claims("firefox", "firefox"));
        // The documented residual: Chocolatey exposes only its package id, so an entry the
        // underlying installer registered under the vendor's own name isn't recognized. See
        // scan_chocolatey.
        assert!(!keys.claims(r"Mozilla Firefox 154.0.1 (x64 en-US)", "Mozilla Firefox"));
    }

    #[test]
    fn managed_keys_ignores_blank_keys() {
        // A listing row with an empty column must not contribute a key that matches every registry
        // entry with an empty display name.
        let mut keys = ManagedKeys::default();
        keys.insert("");
        keys.insert("   ");

        assert_eq!(keys.len(), 0);
        assert!(!keys.claims("", ""));
    }

    #[test]
    fn parse_choco_list_reads_id_and_version() {
        let parsed = parse_choco_list("firefox|154.0.1\r\n7zip|24.9.0\r\n");

        assert_eq!(parsed, vec![("firefox".to_string(), "154.0.1".to_string()), ("7zip".to_string(), "24.9.0".to_string())]);
    }

    #[test]
    fn parse_choco_list_ignores_lines_that_are_not_id_version_pairs() {
        let parsed = parse_choco_list("Chocolatey v2.7.4\nfirefox|154.0.1\n\n");

        assert_eq!(parsed, vec![("firefox".to_string(), "154.0.1".to_string())]);
    }

    #[test]
    fn parse_choco_outdated_takes_the_available_version_not_the_installed_one() {
        // The columns are id|current|available|pinned — reading the wrong one would report every
        // outdated package as already current.
        let parsed = parse_choco_outdated("firefox|153.0|154.0.1|false\n");

        assert_eq!(parsed.get("firefox"), Some(&"154.0.1".to_string()));
    }

    #[test]
    fn parse_choco_outdated_ignores_a_malformed_line() {
        let parsed = parse_choco_outdated("Chocolatey v2.7.4\nfirefox|153.0|154.0.1|false\nbroken|only-two\n");

        assert_eq!(parsed.len(), 1);
        assert!(parsed.contains_key("firefox"));
    }
}
