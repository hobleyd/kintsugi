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
///
/// Deliberately identical to the Linux agent's `PLACEHOLDER_SERIALS`: both agents read the same
/// SMBIOS field and inherit the same junk from the same board vendors, so a string worth screening
/// on one is worth screening on the other. Kept identical rather than trimmed, even though the
/// length floor in [`is_usable_serial_number`] makes `"0"`, `"na"`, `"n/a"` and `"oem"` unreachable
/// here (and covers Linux's `""` and `"1"`, which is why those two are absent) — the floor is the
/// kind of thing that gets relaxed later, and a list that has to be re-derived from another
/// platform's is how the two drift apart.
const PLACEHOLDER_SERIAL_NUMBERS: &[&str] = &[
    "to be filled by o.e.m.",
    "to be filled by o.e.m",
    "fill by oem",
    "oem",
    "system serial number",
    "chassis serial number",
    "default string",
    "none",
    "empty",
    "n/a",
    "na",
    "not applicable",
    "not specified",
    "0",
    "0123456789",
    "123456789",
    "xxxxxxx",
    "invalid",
    "unknown",
];

/// SMBIOS system UUIDs that are constants rather than identities, screened separately because the
/// UUID is a candidate identity in its own right (see [`choose_serial_number`]).
///
/// The all-zero form is caught by the filler screening in [`is_usable_serial_number`] on its own;
/// these two are not. `FFFFFFFF-...` means the vendor left the field out, and
/// `03000200-0400-0500-0006-000700080009` is a fixed value shipped by some VMware and Dell
/// firmware — structurally plausible, and identical on every machine carrying it, so accepting it
/// would enroll a whole fleet as one host.
const PLACEHOLDER_SYSTEM_UUIDS: &[&str] = &[
    "00000000-0000-0000-0000-000000000000",
    "ffffffff-ffff-ffff-ffff-ffffffffffff",
    "03000200-0400-0500-0006-000700080009",
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
    if trimmed.chars().all(|c| c == trimmed.chars().next().unwrap_or(' ')) {
        return false;
    }
    // The same filler character set the Linux agent's `usable_serial` screens on, which catches
    // mixed fillers ("0-0-0-0", "0000.0000") that the single-character rule above lets through.
    !trimmed.chars().all(|c| matches!(c, '0' | 'x' | 'X' | '.' | '-' | ' '))
}

/// Screens a candidate SMBIOS system UUID: everything [`is_usable_serial_number`] rejects, plus the
/// vendor constants in [`PLACEHOLDER_SYSTEM_UUIDS`].
fn is_usable_system_uuid(candidate: &str) -> bool {
    is_usable_serial_number(candidate) && !PLACEHOLDER_SYSTEM_UUIDS.contains(&candidate.trim().to_lowercase().as_str())
}

/// Labels for the sources [`choose_serial_number`] picks between, so the log names exactly which
/// field this host's identity came from — the first question worth asking when a host enrolls under
/// something an administrator doesn't recognize.
const REGISTRY_SERIAL_SOURCE: &str = r"HKLM\HARDWARE\DESCRIPTION\System\BIOS\SystemSerialNumber";
const MACHINE_GUID_SOURCE: &str = r"HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid";

/// The firmware identity fields this host's SMBIOS tables carry, read in one pass by
/// [`read_firmware_identity`]. Every field is optional: a class can be absent, an instance can be
/// missing, and a vendor can leave any individual field blank — which is the whole reason
/// [`serial_number`] walks a chain rather than trusting one source.
#[derive(Debug, Default, PartialEq, Eq)]
struct FirmwareIdentity {
    /// The serial Windows reports on `Win32_BIOS` — in practice the system serial, and the value an
    /// administrator reads off the chassis sticker. Observed populated on hardware whose
    /// [`REGISTRY_SERIAL_SOURCE`] value is absent, which is why this chain exists at all.
    bios_serial: Option<String>,
    /// SMBIOS type 1 system serial (`Win32_ComputerSystemProduct.IdentifyingNumber`) — the same
    /// field [`REGISTRY_SERIAL_SOURCE`] is populated from, asked for the other way round.
    product_serial: Option<String>,
    /// SMBIOS type 2 baseboard serial, and reachable *only* here: the BIOS registry key exposes the
    /// baseboard's manufacturer, product and version and no serial at all, so the registry-only
    /// path could never have found it however hard it looked. Whitebox builds and plenty of OEM
    /// desktops leave the system serial blank with a real board serial present.
    baseboard_serial: Option<String>,
    /// SMBIOS type 3 chassis serial. `SMBIOSAssetTag` sits beside it on the same class and is
    /// deliberately *not* read: an asset tag is administrator-assigned, frequently identical across
    /// a purchase batch, and identity is the one thing that must not be shared.
    enclosure_serial: Option<String>,
    /// SMBIOS type 1 system UUID. Not a serial, but per-machine, populated on nearly all physical
    /// hardware, and set per-VM by every hypervisor — see [`choose_serial_number`] for why it
    /// outranks the `MachineGuid`.
    uuid: Option<String>,
}

/// Reads all five firmware identity fields in one PowerShell pass. Five separate `Get-CimInstance`
/// invocations would cost five process starts on the critical path of service startup (`Agent::new`
/// resolves the serial before it can do anything else), and this is only reached on a host whose
/// registry serial is already missing.
///
/// Three details are load-bearing:
///
/// * `-ErrorAction SilentlyContinue` per query rather than one `$ErrorActionPreference = 'Stop'` —
///   `Win32_SystemEnclosure` is absent on some virtual hardware, and stopping at the first failure
///   would throw away a `Win32_BIOS` serial that had already been read successfully.
/// * `Select-Object -First 1` — `Win32_SystemEnclosure` and `Win32_BaseBoard` are multi-instance
///   classes, and an unpinned query on a two-enclosure chassis makes `ConvertTo-Json` emit an
///   *array* where an object is expected. Exactly the shape of failure the `winget list` parser
///   below exists to survive, so it is pinned here and tolerated in [`parse_firmware_identity`].
/// * CIM, not WMI (`Get-WmiObject`) — the `wmic` CLI was removed in Windows 11 24H2 and
///   `Get-WmiObject` is deprecated alongside it; `Get-CimInstance` is what remains.
const FIRMWARE_IDENTITY_SCRIPT: &str = r#"
$product = Get-CimInstance Win32_ComputerSystemProduct -ErrorAction SilentlyContinue | Select-Object -First 1
$bios = Get-CimInstance Win32_BIOS -ErrorAction SilentlyContinue | Select-Object -First 1
$baseboard = Get-CimInstance Win32_BaseBoard -ErrorAction SilentlyContinue | Select-Object -First 1
$enclosure = Get-CimInstance Win32_SystemEnclosure -ErrorAction SilentlyContinue | Select-Object -First 1
[pscustomobject]@{
    biosSerial = $bios.SerialNumber
    productSerial = $product.IdentifyingNumber
    baseboardSerial = $baseboard.SerialNumber
    enclosureSerial = $enclosure.SerialNumber
    uuid = $product.UUID
} | ConvertTo-Json -Compress
"#;

fn read_firmware_identity() -> FirmwareIdentity {
    match crate::os_update::run_powershell(FIRMWARE_IDENTITY_SCRIPT) {
        Ok(output) if output.status.success() => parse_firmware_identity(&String::from_utf8_lossy(&output.stdout)),
        Ok(output) => {
            crate::logging::warn(&format!(
                "could not read this machine's SMBIOS identity fields — PowerShell exited with {}: {}",
                output.status,
                String::from_utf8_lossy(&output.stderr).trim()
            ));
            FirmwareIdentity::default()
        }
        Err(err) => {
            crate::logging::warn(&format!("could not run PowerShell to read this machine's SMBIOS identity fields: {err:#}"));
            FirmwareIdentity::default()
        }
    }
}

/// Parses [`FIRMWARE_IDENTITY_SCRIPT`]'s output. Takes a `&str` so the real captured shapes — an
/// object, an array, every field null — are exercised by tests, the same reason the `winget list`
/// and `choco list` parsers below do.
fn parse_firmware_identity(json: &str) -> FirmwareIdentity {
    let Ok(parsed) = serde_json::from_str::<serde_json::Value>(json.trim()) else {
        crate::logging::warn(&format!(
            "could not parse the SMBIOS identity fields PowerShell reported: {}",
            json.trim()
        ));
        return FirmwareIdentity::default();
    };

    // An array is tolerated rather than expected: the script pins one instance per class, but an
    // array is what an unpinned multi-instance query produces, and reading its first element beats
    // reporting nothing at all if that pinning is ever lost.
    let fields = match &parsed {
        serde_json::Value::Array(items) => items.first(),
        other => Some(other),
    };
    let Some(fields) = fields else {
        return FirmwareIdentity::default();
    };

    // `ConvertTo-Json` writes a blank or absent SMBIOS field as `null` or as an empty string
    // depending on the vendor; both mean "nothing here", and neither should reach the screening as
    // a candidate.
    let field = |name: &str| {
        fields
            .get(name)
            .and_then(serde_json::Value::as_str)
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(str::to_string)
    };

    FirmwareIdentity {
        bios_serial: field("biosSerial"),
        product_serial: field("productSerial"),
        baseboard_serial: field("baseboardSerial"),
        enclosure_serial: field("enclosureSerial"),
        uuid: field("uuid"),
    }
}

/// Which source [`serial_number`] settled on, and the value it yielded.
#[derive(Debug, PartialEq, Eq)]
struct SerialNumberChoice<'a> {
    source: &'a str,
    value: &'a str,
}

/// The ordered choice at the heart of [`serial_number`], split out from the reading so the ordering
/// itself is unit-testable: none of these sources can be faked under mingw+Wine, which runs this
/// crate's tests but has neither a registry nor CIM (see the Cargo.toml notes and CLAUDE.md).
///
/// The order is by how well each field identifies *this physical machine*, and it deliberately puts
/// both hardware serials the vendor may have set ahead of anything Windows generated:
///
/// 1. the registry system serial — what an administrator sees on the sticker and in every asset
///    system, and the closest analogue to what the macOS agent reports;
/// 2. `Win32_BIOS.SerialNumber`, then `Win32_ComputerSystemProduct.IdentifyingNumber` — the same
///    field by two other routes, either of which can be populated where the registry value is not;
/// 3. the baseboard, then the chassis serial — a different physical part than the one the asset
///    system names, but a real vendor-set serial for this machine;
/// 4. the SMBIOS system UUID — not a serial, but per-machine and per-VM;
/// 5. the `MachineGuid`, last and reluctantly. See [`machine_guid`].
fn choose_serial_number<'a>(
    registry_serial: Option<&'a str>,
    firmware: &'a FirmwareIdentity,
    machine_guid: Option<&'a str>,
) -> Option<SerialNumberChoice<'a>> {
    let serial_candidates: [(&'a str, Option<&'a str>); 5] = [
        (REGISTRY_SERIAL_SOURCE, registry_serial),
        ("Win32_BIOS.SerialNumber", firmware.bios_serial.as_deref()),
        ("Win32_ComputerSystemProduct.IdentifyingNumber", firmware.product_serial.as_deref()),
        ("Win32_BaseBoard.SerialNumber", firmware.baseboard_serial.as_deref()),
        ("Win32_SystemEnclosure.SerialNumber", firmware.enclosure_serial.as_deref()),
    ];

    for (source, candidate) in serial_candidates {
        if let Some(value) = candidate.map(str::trim).filter(|value| is_usable_serial_number(value)) {
            return Some(SerialNumberChoice { source, value });
        }
    }

    if let Some(value) = firmware.uuid.as_deref().map(str::trim).filter(|value| is_usable_system_uuid(value)) {
        return Some(SerialNumberChoice { source: "Win32_ComputerSystemProduct.UUID", value });
    }

    machine_guid
        .map(str::trim)
        .filter(|value| is_usable_serial_number(value))
        .map(|value| SerialNumberChoice { source: MACHINE_GUID_SOURCE, value })
}

/// This host's stable unique identifier: the vendor-set hardware serial wherever one can be found,
/// and a Windows-generated identifier only when none can. See [`choose_serial_number`] for the
/// order and the reasoning behind it.
///
/// Returns an error rather than any fallback value if every source comes up empty. Enrolling under
/// a non-unique identifier is worse than not enrolling: it would silently merge this host's record
/// with another machine's.
///
/// Called exactly once per process, by `Agent::new` — which is what makes the PowerShell pass in
/// [`read_firmware_identity`] affordable.
pub fn serial_number() -> Result<String> {
    let registry_serial = smbios_serial_number();

    // Only asked for once the registry has come up empty: on a machine whose vendor set the field
    // properly there is nothing to go looking for, and this costs a process start.
    let firmware = match &registry_serial {
        Some(_) => FirmwareIdentity::default(),
        None => {
            crate::logging::warn(&format!(
                "{REGISTRY_SERIAL_SOURCE} holds no usable serial number (absent, or a manufacturer placeholder) — \
                 reading this machine's SMBIOS identity fields via CIM instead"
            ));
            read_firmware_identity()
        }
    };

    let machine_guid = machine_guid();

    let choice = choose_serial_number(registry_serial.as_deref(), &firmware, machine_guid.as_deref()).context(
        "could not determine a unique identifier for this machine — no SMBIOS serial number, system UUID or Windows \
         MachineGuid is usable, and enrolling under a non-unique one would merge this host's record with another machine's",
    )?;

    if choice.source != REGISTRY_SERIAL_SOURCE {
        crate::logging::warn(&format!(
            "this host's identity comes from {} rather than its SMBIOS system serial number",
            choice.source
        ));
    }

    if choice.source == MACHINE_GUID_SOURCE {
        crate::logging::warn(
            "no SMBIOS field on this machine carries a usable identity, so the MachineGuid is being used — which is \
             unique only if this machine's image was deployed with sysprep. Set a real serial number in the firmware \
             (or in the hypervisor's configuration) if hosts start sharing a record.",
        );
    }

    Ok(choice.value.to_string())
}

fn smbios_serial_number() -> Option<String> {
    let value: String = RegKey::predef(HKEY_LOCAL_MACHINE)
        .open_subkey_with_flags(r"HARDWARE\DESCRIPTION\System\BIOS", KEY_READ)
        .ok()?
        .get_value("SystemSerialNumber")
        .ok()?;

    is_usable_serial_number(&value).then(|| value.trim().to_string())
}

/// The last resort, and the weakest of them: `MachineGuid` identifies a Windows *installation*, not
/// a machine. Sysprep regenerates it, so an image deployed properly gives every clone its own — but
/// an image deployed *without* sysprep gives every clone the same one, and those are exactly the
/// machines whose SMBIOS serial is a vendor placeholder too. It is also lost on a rebuild, which
/// re-enrolls the host as a new record.
///
/// So it sits below every SMBIOS field including the system UUID, rather than immediately below the
/// registry serial as it once did. The Linux agent's equivalent fallback (`/etc/machine-id`) has the
/// same shape and the same caveat, one rung better handled: `systemd-firstboot` regenerates it when
/// an image is cloned properly.
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
        for placeholder in [
            "To Be Filled By O.E.M.",
            "Fill By OEM",
            "System Serial Number",
            "Chassis Serial Number",
            "Default string",
            "None",
            "Empty",
            "OEM",
            "NA",
            "0",
            "Unknown",
        ] {
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

    #[test]
    fn is_usable_serial_number_rejects_mixed_filler() {
        // Not a single repeated character, so the rule above lets these through; they are still
        // nobody's serial number. The Linux agent screens the same character set.
        assert!(!is_usable_serial_number("0-0-0-0"));
        assert!(!is_usable_serial_number("0000.0000"));
        assert!(!is_usable_serial_number("--------"));
    }

    #[test]
    fn is_usable_system_uuid_rejects_the_vendor_constants() {
        // The all-zero form is already filler; the other two are structurally plausible and shipped
        // on real (and virtual) hardware, so every host carrying one would enroll as the same host.
        for constant in [
            "00000000-0000-0000-0000-000000000000",
            "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF",
            "03000200-0400-0500-0006-000700080009",
        ] {
            assert!(!is_usable_system_uuid(constant), "{constant} should be rejected");
        }
    }

    #[test]
    fn is_usable_system_uuid_accepts_a_real_uuid() {
        assert!(is_usable_system_uuid("4C4C4544-0043-4410-8058-CAC04F573233"));
    }

    /// `ConvertTo-Json -Compress` output captured from a real Windows 11 host — the one whose
    /// registry `SystemSerialNumber` is absent while `Win32_BIOS.SerialNumber` carries the sticker
    /// serial, which is the case this whole chain was added for.
    const FIRMWARE_IDENTITY_SAMPLE: &str = concat!(
        r#"{"biosSerial":"5CD6106928","productSerial":"5CD6106928","baseboardSerial":"PGVQC02T4CX0AL","#,
        r#""enclosureSerial":"5CD6106928","uuid":"4C4C4544-0043-4410-8058-CAC04F573233"}"#,
        "\r\n",
    );

    #[test]
    fn parse_firmware_identity_reads_every_field() {
        assert_eq!(
            parse_firmware_identity(FIRMWARE_IDENTITY_SAMPLE),
            FirmwareIdentity {
                bios_serial: Some("5CD6106928".to_string()),
                product_serial: Some("5CD6106928".to_string()),
                baseboard_serial: Some("PGVQC02T4CX0AL".to_string()),
                enclosure_serial: Some("5CD6106928".to_string()),
                uuid: Some("4C4C4544-0043-4410-8058-CAC04F573233".to_string()),
            }
        );
    }

    #[test]
    fn parse_firmware_identity_reads_the_first_element_of_an_array() {
        // What an unpinned multi-instance query produces. The script pins `-First 1` so this should
        // not arise, but parsing it beats reporting no identity at all if that pinning is lost.
        let json = r#"[{"biosSerial":"5CD6106928","productSerial":null,"baseboardSerial":null,"enclosureSerial":null,"uuid":null},
                       {"biosSerial":"OTHER","productSerial":null,"baseboardSerial":null,"enclosureSerial":null,"uuid":null}]"#;

        assert_eq!(parse_firmware_identity(json).bios_serial, Some("5CD6106928".to_string()));
    }

    #[test]
    fn parse_firmware_identity_treats_null_and_blank_fields_as_absent() {
        // A vendor that left the field out gives `null`; one that wrote whitespace into it gives an
        // empty string. Neither should reach the screening as a candidate.
        let json = r#"{"biosSerial":null,"productSerial":"","baseboardSerial":"   ","enclosureSerial":null,"uuid":null}"#;

        assert_eq!(parse_firmware_identity(json), FirmwareIdentity::default());
    }

    #[test]
    fn parse_firmware_identity_returns_nothing_for_output_that_is_not_json() {
        // PowerShell writing an error to stdout, or nothing at all. Reporting no fields sends
        // `serial_number` on to the MachineGuid rather than panicking on the critical startup path.
        assert_eq!(parse_firmware_identity(""), FirmwareIdentity::default());
        assert_eq!(parse_firmware_identity("Get-CimInstance : Access denied"), FirmwareIdentity::default());
    }

    #[test]
    fn choose_serial_number_prefers_the_registry_serial() {
        let firmware = FirmwareIdentity { bios_serial: Some("5CD6106928".to_string()), ..Default::default() };
        let choice = choose_serial_number(Some("PF3K2N9B"), &firmware, Some("9d8f6b1e-0000-4a11-b2c3-5566778899aa")).unwrap();

        assert_eq!(choice.value, "PF3K2N9B");
        assert_eq!(choice.source, REGISTRY_SERIAL_SOURCE);
    }

    #[test]
    fn choose_serial_number_falls_through_a_placeholder_registry_value_to_the_bios_serial() {
        // The case this chain exists for: the registry value is absent or junk while `Win32_BIOS`
        // carries the serial printed on the chassis.
        let firmware = FirmwareIdentity { bios_serial: Some("5CD6106928".to_string()), ..Default::default() };

        for registry in [None, Some("To Be Filled By O.E.M."), Some("Default string")] {
            let choice = choose_serial_number(registry, &firmware, Some("9d8f6b1e-0000-4a11-b2c3-5566778899aa")).unwrap();
            assert_eq!(choice.value, "5CD6106928", "registry value {registry:?} should have been skipped");
            assert_eq!(choice.source, "Win32_BIOS.SerialNumber");
        }
    }

    #[test]
    fn choose_serial_number_walks_the_whole_chain_in_order() {
        let firmware = FirmwareIdentity {
            bios_serial: Some("Default string".to_string()),
            product_serial: None,
            baseboard_serial: Some("PGVQC02T4CX0AL".to_string()),
            enclosure_serial: Some("5CD6106928".to_string()),
            uuid: Some("4C4C4544-0043-4410-8058-CAC04F573233".to_string()),
        };
        let choice = choose_serial_number(None, &firmware, Some("9d8f6b1e-0000-4a11-b2c3-5566778899aa")).unwrap();

        // The baseboard serial, not the chassis one and not the UUID: a real vendor-set serial
        // outranks both, and the order between them is fixed so a host cannot change identity
        // depending on which field a later firmware update happens to fill in.
        assert_eq!(choice.value, "PGVQC02T4CX0AL");
        assert_eq!(choice.source, "Win32_BaseBoard.SerialNumber");
    }

    #[test]
    fn choose_serial_number_prefers_the_system_uuid_over_the_machine_guid() {
        // The MachineGuid identifies a Windows installation; the UUID identifies the machine (and,
        // under a hypervisor, the VM). See `machine_guid` for why that ordering matters.
        let firmware = FirmwareIdentity { uuid: Some("4C4C4544-0043-4410-8058-CAC04F573233".to_string()), ..Default::default() };
        let choice = choose_serial_number(None, &firmware, Some("9d8f6b1e-0000-4a11-b2c3-5566778899aa")).unwrap();

        assert_eq!(choice.value, "4C4C4544-0043-4410-8058-CAC04F573233");
        assert_eq!(choice.source, "Win32_ComputerSystemProduct.UUID");
    }

    #[test]
    fn choose_serial_number_falls_back_to_the_machine_guid_when_the_uuid_is_a_constant() {
        let firmware = FirmwareIdentity {
            enclosure_serial: Some("Chassis Serial Number".to_string()),
            uuid: Some("03000200-0400-0500-0006-000700080009".to_string()),
            ..Default::default()
        };
        let choice = choose_serial_number(None, &firmware, Some("9d8f6b1e-0000-4a11-b2c3-5566778899aa")).unwrap();

        assert_eq!(choice.value, "9d8f6b1e-0000-4a11-b2c3-5566778899aa");
        assert_eq!(choice.source, MACHINE_GUID_SOURCE);
    }

    #[test]
    fn choose_serial_number_refuses_to_invent_an_identity() {
        // Every source a placeholder and no MachineGuid: `serial_number` turns this into an error
        // and the agent does not enroll, rather than sharing a record with another machine.
        let firmware = FirmwareIdentity {
            bios_serial: Some("To Be Filled By O.E.M.".to_string()),
            product_serial: Some("Not Specified".to_string()),
            baseboard_serial: Some("Default string".to_string()),
            enclosure_serial: Some("0000000000".to_string()),
            uuid: Some("00000000-0000-0000-0000-000000000000".to_string()),
        };

        assert_eq!(choose_serial_number(Some("None"), &firmware, None), None);
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
