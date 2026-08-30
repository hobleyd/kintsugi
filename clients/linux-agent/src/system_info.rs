use std::collections::HashMap;
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
    /// Name of another reported app that manages this one (e.g. "Flatpak"
    /// for a Flatpak application). Must match that app's own `name` exactly.
    #[serde(rename = "packageManager", skip_serializing_if = "Option::is_none")]
    pub package_manager: Option<String>,
    /// The manager's own identifier for this application — a Flatpak application ID
    /// ("org.mozilla.firefox") or a snap name ("firefox"). Always populated here, unlike the
    /// macOS agent's Homebrew entries which leave it unset: the server hands this straight back
    /// to the upgrade script as `--appId` (see the *UpgradeScript builders), and `upgrade`'s own
    /// `is_patchable` refuses to run a `Script` row that has none.
    #[serde(rename = "applicationIdentifier", skip_serializing_if = "Option::is_none")]
    pub application_identifier: Option<String>,
    /// The latest version available, when known independently of any upgrade research — read
    /// straight from the manager's own catalog. Lets the backend tell whether an update is
    /// available without having to research it separately (see
    /// `RegisterApplicationsCommandHandler.UpsertPackageManagerUpgradePathsAsync`).
    #[serde(rename = "availableVersion", skip_serializing_if = "Option::is_none")]
    pub available_version: Option<String>,
}

/// Returns the machine's local hostname (e.g. "web-01.example.com").
pub fn hostname() -> Result<String> {
    gethostname::gethostname()
        .into_string()
        .map_err(|raw| anyhow::anyhow!("hostname is not valid UTF-8: {raw:?}"))
}

/// Values firmware vendors ship in the DMI serial field instead of a real serial number. Exactly
/// the problem the Windows agent documents at length for `Win32_BIOS.SerialNumber`, and for the
/// same reason: both read the same SMBIOS structure, so both inherit whatever the board vendor
/// left in it. The serial *is* this host's identity — it becomes the certificate CN, which
/// `[RequireAgentIdentity]` compares against every request body — so two hosts sharing one would
/// share a host record, a certificate, and each other's data.
///
/// Virtual machines make this worse on Linux than it is on Macs: a bare `qemu-system-x86_64` with
/// no `-smbios` argument reports "Not Specified" for every guest on the host.
const PLACEHOLDER_SERIALS: &[&str] = &[
    "",
    "0",
    "1",
    "123456789",
    "to be filled by o.e.m.",
    "to be filled by o.e.m",
    "system serial number",
    "default string",
    "not specified",
    "not applicable",
    "none",
    "n/a",
    "na",
    "unknown",
    "invalid",
    "chassis serial number",
    "empty",
    "oem",
    "xxxxxxx",
    "0123456789",
    "00000000",
    "fill by oem",
];

/// Where the kernel exposes the SMBIOS/DMI system serial. Mode 0400 root-only, which is fine:
/// every caller of [`serial_number`] runs as root (see `queue` for why the per-user half of this
/// agent never needs the serial at all).
const DMI_SERIAL_PATH: &str = "/sys/class/dmi/id/product_serial";

/// systemd's per-installation identifier, and the fallback when DMI carries a placeholder. Stable
/// across reboots, regenerated when an image is cloned properly (`systemd-firstboot`), and present
/// on every systemd host — which this agent already requires, since its whole service layer is
/// systemd units. `/var/lib/dbus/machine-id` is checked too because a handful of distributions
/// still make `/etc/machine-id` the symlink rather than the file.
const MACHINE_ID_PATHS: &[&str] = &["/etc/machine-id", "/var/lib/dbus/machine-id"];

/// Returns this host's stable hardware identity: the SMBIOS system serial where the vendor
/// actually set one, and systemd's machine ID where it didn't.
///
/// Refuses to invent a value rather than falling back to something non-unique — see
/// [`PLACEHOLDER_SERIALS`] for what that would cost. macOS has no equivalent failure mode (its
/// serial is always present and always real), which is why only this agent and the Windows one
/// screen for it.
pub fn serial_number() -> Result<String> {
    if let Some(serial) = read_dmi_serial(Path::new(DMI_SERIAL_PATH)) {
        return Ok(serial);
    }

    crate::logging::warn(&format!(
        "{DMI_SERIAL_PATH} holds no usable serial number (a placeholder, empty, or unreadable) — falling back to the machine ID"
    ));

    for path in MACHINE_ID_PATHS {
        if let Some(machine_id) = read_machine_id(Path::new(path)) {
            crate::logging::info(&format!("using the machine ID from {path} as this host's serial number"));
            return Ok(machine_id);
        }
    }

    anyhow::bail!(
        "could not determine a unique identity for this host: {DMI_SERIAL_PATH} holds no real serial number and no machine ID \
         was readable at {}. Refusing to enroll rather than sharing an identity with another host — set a real serial in the \
         firmware/hypervisor, or run `systemd-machine-id-setup`.",
        MACHINE_ID_PATHS.join(" or ")
    )
}

/// The file-reading half of [`serial_number`]'s DMI check, split out so the placeholder screening
/// can be exercised directly. Returns `None` for anything that isn't a real, usable serial.
fn read_dmi_serial(path: &Path) -> Option<String> {
    let raw = fs::read_to_string(path).ok()?;
    usable_serial(&raw)
}

/// Screens one candidate serial. Deliberately strict: anything that survives becomes a
/// certificate CN, so it's better to fall through to the machine ID than to accept a value shared
/// with every other host from the same vendor.
fn usable_serial(raw: &str) -> Option<String> {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return None;
    }

    let normalized = trimmed.to_lowercase();
    if PLACEHOLDER_SERIALS.contains(&normalized.as_str()) {
        return None;
    }

    // Catches the whole family of "0000...", "XXXX...", "....." fillers without needing every
    // length of each of them listed above.
    if trimmed.chars().all(|c| c == '0' || c == 'x' || c == 'X' || c == '.' || c == '-' || c == ' ') {
        return None;
    }

    Some(trimmed.to_string())
}

fn read_machine_id(path: &Path) -> Option<String> {
    let raw = fs::read_to_string(path).ok()?;
    let trimmed = raw.trim();
    // A machine ID is exactly 32 lowercase hex characters. An "uninitialized" one is empty, and
    // some container runtimes leave it as literal "uninitialized\n".
    (trimmed.len() == 32 && trimmed.chars().all(|c| c.is_ascii_hexdigit())).then(|| trimmed.to_string())
}

/// Returns a human-readable OS name and version, e.g. "Ubuntu 24.04.1 LTS (Linux)".
///
/// The "(Linux)" suffix is not decoration and must not be dropped: `PlatformBucket.From` on the
/// server buckets a host by substring-matching this string, and its Linux arm looks for "linux",
/// "ubuntu", "debian", "centos" or "fedora". A perfectly ordinary `PRETTY_NAME` like
/// "openSUSE Leap 15.6" or "Alpine v3.20" matches none of them and would fall through to the
/// `generic` bucket — which is the shared-bucket hazard the `SplitPackageManagerPlatformBucket`
/// migration exists to have killed. Guaranteeing the word here fixes it for every distribution at
/// once, including ones nobody has thought of yet, rather than growing that substring list
/// forever.
pub fn operating_system() -> Result<String> {
    let pretty_name = read_os_release_pretty_name(Path::new("/etc/os-release"))
        .or_else(|| read_os_release_pretty_name(Path::new("/usr/lib/os-release")))
        .context("could not read PRETTY_NAME from /etc/os-release or /usr/lib/os-release")?;

    Ok(ensure_mentions_linux(&pretty_name))
}

fn read_os_release_pretty_name(path: &Path) -> Option<String> {
    parse_os_release_pretty_name(&fs::read_to_string(path).ok()?)
}

/// Pulls `PRETTY_NAME` out of an os-release file. The format is shell-ish: `KEY=value`, with the
/// value optionally quoted, one per line, comments allowed.
fn parse_os_release_pretty_name(contents: &str) -> Option<String> {
    contents.lines().find_map(|line| {
        let value = line.trim().strip_prefix("PRETTY_NAME=")?;
        let unquoted = value.trim().trim_matches('"').trim_matches('\'').trim();
        (!unquoted.is_empty()).then(|| unquoted.to_string())
    })
}

/// Appends "(Linux)" unless the name already says it — see [`operating_system`] for why this
/// matters. "Debian GNU/Linux 12" and "Arch Linux" are left exactly as they are.
fn ensure_mentions_linux(pretty_name: &str) -> String {
    if pretty_name.to_lowercase().contains("linux") {
        pretty_name.to_string()
    } else {
        format!("{pretty_name} (Linux)")
    }
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

/// Names reported for the managers themselves, and the `packageManager` value every application
/// they manage is tagged with — the backend links a child to its manager by matching these
/// against another entry's `name` in the same report, and `PackageManagerCatalog` recognizes a
/// manager by this exact string. A rename on either side silently stops an entire manager's
/// applications resolving.
const FLATPAK_NAME: &str = "Flatpak";
const SNAP_NAME: &str = "Snap";

/// Reports Flatpak itself (with its own version) plus every application installed into the
/// *system* installation, each tagged as managed by "Flatpak". Returns an empty list (not an
/// error) if Flatpak isn't installed.
///
/// System installations only, deliberately. A `flatpak --user` install belongs to one user's home
/// directory and is invisible to root — and even if it were reported, the root service is what
/// runs upgrades here (see `queue`), so it could never patch one. Reporting an application this
/// agent cannot act on would show up as a permanently out-of-date row nobody can fix.
pub fn scan_flatpak() -> Vec<InstalledApp> {
    let Some(flatpak) = find_binary("flatpak") else {
        return Vec::new();
    };

    let mut apps = Vec::new();

    match run_capturing(&flatpak, &["--version"]) {
        Ok(stdout) => match parse_flatpak_version(&stdout) {
            Some(version) => apps.push(InstalledApp {
                name: FLATPAK_NAME.to_string(),
                version,
                package_manager: None,
                application_identifier: Some("flatpak".to_string()),
                available_version: None,
            }),
            None => crate::logging::warn(&format!("unexpected `flatpak --version` output format: {stdout:?}")),
        },
        Err(err) => crate::logging::warn(&format!("could not determine Flatpak's own version: {err:#}")),
    }

    let available = match run_capturing(&flatpak, &["remote-ls", "--system", "--updates", "--columns=application,version"]) {
        Ok(stdout) => parse_flatpak_updates(&stdout),
        Err(err) => {
            // Best-effort, exactly like `brew info` on macOS: this only enriches entries the
            // listing below already reports, so a failure here costs an available-version column,
            // not the scan.
            crate::logging::warn(&format!("could not list available Flatpak updates: {err:#}"));
            HashMap::new()
        }
    };

    match run_capturing(&flatpak, &["list", "--system", "--app", "--columns=name,application,version"]) {
        Ok(stdout) => apps.extend(parse_flatpak_list(&stdout, &available)),
        Err(err) => crate::logging::warn(&format!("could not list installed Flatpak applications: {err:#}")),
    }

    apps
}

/// Parses Flatpak's own version from `flatpak --version`, whose only line looks like
/// "Flatpak 1.14.4".
fn parse_flatpak_version(stdout: &str) -> Option<String> {
    stdout
        .lines()
        .next()?
        .split_whitespace()
        .nth(1)
        .map(str::to_string)
}

/// Parses `flatpak remote-ls --updates --columns=application,version` into application-id ->
/// available-version. Only applications with an update pending appear at all, so an id missing
/// from this map simply has no newer version known.
fn parse_flatpak_updates(stdout: &str) -> HashMap<String, String> {
    stdout
        .lines()
        .filter_map(|line| {
            let mut columns = line.split('\t');
            let application = columns.next()?.trim();
            let version = columns.next()?.trim();
            (!application.is_empty() && !version.is_empty()).then(|| (application.to_string(), version.to_string()))
        })
        .collect()
}

/// Parses `flatpak list --columns=name,application,version`. Columns are tab-separated (a display
/// name legitimately contains spaces, so nothing else would do), and an application with no
/// version recorded — which happens for some remotes — is reported as "unknown" rather than
/// dropped, matching how the macOS agent treats a bundle with no version string.
fn parse_flatpak_list(stdout: &str, available: &HashMap<String, String>) -> Vec<InstalledApp> {
    stdout
        .lines()
        .filter_map(|line| {
            let mut columns = line.split('\t');
            let name = columns.next()?.trim();
            let application = columns.next()?.trim();
            let version = columns.next().unwrap_or("").trim();

            if name.is_empty() || application.is_empty() {
                return None;
            }

            Some(InstalledApp {
                name: name.to_string(),
                version: if version.is_empty() { "unknown".to_string() } else { version.to_string() },
                package_manager: Some(FLATPAK_NAME.to_string()),
                application_identifier: Some(application.to_string()),
                available_version: available.get(application).cloned(),
            })
        })
        .collect()
}

/// Reports snapd itself (as "Snap", with snapd's version) plus every installed snap, each tagged
/// as managed by "Snap". Returns an empty list (not an error) if snapd isn't installed.
pub fn scan_snap() -> Vec<InstalledApp> {
    let Some(snap) = find_binary("snap") else {
        return Vec::new();
    };

    let mut apps = Vec::new();

    match run_capturing(&snap, &["version"]) {
        Ok(stdout) => match parse_snap_version(&stdout) {
            Some(version) => apps.push(InstalledApp {
                name: SNAP_NAME.to_string(),
                version,
                package_manager: None,
                // "snapd" rather than "snap": snapd ships as a snap of that name and refreshes
                // itself like any other, so this is the id its own upgrade script needs.
                application_identifier: Some("snapd".to_string()),
                available_version: None,
            }),
            None => crate::logging::warn(&format!("unexpected `snap version` output format: {stdout:?}")),
        },
        Err(err) => crate::logging::warn(&format!("could not determine snapd's version: {err:#}")),
    }

    let available = match run_capturing(&snap, &["refresh", "--list"]) {
        Ok(stdout) => parse_snap_table(&stdout),
        Err(err) => {
            crate::logging::warn(&format!("could not list available snap refreshes: {err:#}"));
            HashMap::new()
        }
    };

    match run_capturing(&snap, &["list"]) {
        Ok(stdout) => {
            apps.extend(parse_snap_table(&stdout).into_iter().map(|(name, version)| InstalledApp {
                version,
                package_manager: Some(SNAP_NAME.to_string()),
                application_identifier: Some(name.clone()),
                available_version: available.get(&name).cloned(),
                name,
            }));
        }
        Err(err) => crate::logging::warn(&format!("could not list installed snaps: {err:#}")),
    }

    apps
}

/// Parses the snapd version out of `snap version`, whose output is a small two-column table:
///
/// ```text
/// snap    2.63
/// snapd   2.63
/// series  16
/// ```
///
/// The `snapd` line, not the `snap` one: `snap` is the client binary's version and `snapd` is the
/// daemon's, and it's the daemon that ships as the refreshable "snapd" snap this reports against.
fn parse_snap_version(stdout: &str) -> Option<String> {
    stdout.lines().find_map(|line| {
        let mut fields = line.split_whitespace();
        (fields.next()? == "snapd").then(|| fields.next().map(str::to_string))?
    })
}

/// Parses the name/version columns out of `snap list` or `snap refresh --list`, which share one
/// layout: a header row, then one whitespace-separated row per snap whose first two fields are
/// always the name and the version (neither can contain a space).
///
/// `snap refresh --list` prints "All snaps up to date." and no table when nothing is pending; the
/// header check below is what makes that come back as an empty map rather than a bogus entry.
fn parse_snap_table(stdout: &str) -> HashMap<String, String> {
    stdout
        .lines()
        .skip_while(|line| !line.trim_start().starts_with("Name"))
        .skip(1)
        .filter_map(|line| {
            let mut fields = line.split_whitespace();
            let name = fields.next()?;
            let version = fields.next()?;
            Some((name.to_string(), version.to_string()))
        })
        .collect()
}

/// Locates a tool by name across the directories a root systemd service can actually count on.
/// `PATH` is deliberately not consulted: a unit started by systemd inherits a minimal environment
/// (systemd's own compiled-in default, typically without `/snap/bin`), not the interactive shell's
/// — the same reason the macOS agent hardcodes Homebrew's two possible prefixes rather than
/// looking `brew` up on `PATH`. `/snap/bin` in particular is where Ubuntu puts the `snap` client
/// and is absent from that default on several distributions.
fn find_binary(name: &str) -> Option<PathBuf> {
    ["/usr/bin", "/bin", "/usr/local/bin", "/snap/bin", "/usr/sbin", "/sbin"]
        .into_iter()
        .map(|dir| Path::new(dir).join(name))
        .find(|path| path.is_file())
}

/// Runs a command and returns its stdout, treating a non-zero exit as an error carrying stderr —
/// the shape every scan helper above wants, so none of them repeats it.
fn run_capturing(program: &Path, args: &[&str]) -> Result<String> {
    let output = Command::new(program)
        .args(args)
        .output()
        .with_context(|| format!("failed to run {}", program.display()))?;

    if !output.status.success() {
        anyhow::bail!(
            "{} {} exited with {}: {}",
            program.display(),
            args.join(" "),
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }

    Ok(String::from_utf8_lossy(&output.stdout).to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn usable_serial_accepts_a_real_serial() {
        assert_eq!(usable_serial("  C02XY1234ABC \n"), Some("C02XY1234ABC".to_string()));
    }

    #[test]
    fn usable_serial_rejects_every_known_vendor_placeholder() {
        for placeholder in ["To Be Filled By O.E.M.", "Default string", "System Serial Number", "Not Specified", "0", "None", "  "] {
            assert_eq!(usable_serial(placeholder), None, "{placeholder:?} should not be accepted as a serial number");
        }
    }

    #[test]
    fn usable_serial_rejects_filler_runs_of_a_single_character() {
        assert_eq!(usable_serial("00000000000"), None);
        assert_eq!(usable_serial("XXXXXXXXXXX"), None);
        assert_eq!(usable_serial("..........."), None);
        assert_eq!(usable_serial("-----------"), None);
    }

    #[test]
    fn read_machine_id_accepts_only_a_well_formed_id() {
        let dir = std::env::temp_dir();
        let good = dir.join(format!("kintsugi-machine-id-good-{}", std::process::id()));
        let bad = dir.join(format!("kintsugi-machine-id-bad-{}", std::process::id()));

        fs::write(&good, "4f2a1b8c9d0e4f5a6b7c8d9e0f1a2b3c\n").unwrap();
        fs::write(&bad, "uninitialized\n").unwrap();

        assert_eq!(read_machine_id(&good), Some("4f2a1b8c9d0e4f5a6b7c8d9e0f1a2b3c".to_string()));
        assert_eq!(read_machine_id(&bad), None);

        let _ = fs::remove_file(&good);
        let _ = fs::remove_file(&bad);
    }

    #[test]
    fn parse_os_release_pretty_name_reads_a_quoted_value() {
        let contents = "NAME=\"Ubuntu\"\nVERSION=\"24.04.1 LTS (Noble Numbat)\"\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\nID=ubuntu\n";

        assert_eq!(parse_os_release_pretty_name(contents).as_deref(), Some("Ubuntu 24.04.1 LTS"));
    }

    #[test]
    fn parse_os_release_pretty_name_reads_an_unquoted_value() {
        assert_eq!(parse_os_release_pretty_name("PRETTY_NAME=Arch Linux\n").as_deref(), Some("Arch Linux"));
    }

    #[test]
    fn parse_os_release_pretty_name_returns_none_when_absent() {
        assert_eq!(parse_os_release_pretty_name("NAME=\"Whatever\"\nID=whatever\n"), None);
    }

    /// The whole point of `ensure_mentions_linux` — a distribution whose PRETTY_NAME never says
    /// "Linux" must still land in `PlatformBucket.Linux` rather than `generic`.
    #[test]
    fn ensure_mentions_linux_adds_the_word_when_the_distribution_omits_it() {
        assert_eq!(ensure_mentions_linux("openSUSE Leap 15.6"), "openSUSE Leap 15.6 (Linux)");
        assert_eq!(ensure_mentions_linux("Alpine v3.20"), "Alpine v3.20 (Linux)");
    }

    #[test]
    fn ensure_mentions_linux_leaves_a_name_that_already_says_it_alone() {
        assert_eq!(ensure_mentions_linux("Debian GNU/Linux 12 (bookworm)"), "Debian GNU/Linux 12 (bookworm)");
        assert_eq!(ensure_mentions_linux("Arch Linux"), "Arch Linux");
    }

    /// Every OS string this agent can produce has to reach `PlatformBucket.Linux`, whose Linux arm
    /// matches on these substrings. Encoded here so a change to `ensure_mentions_linux` can't
    /// silently drop a distribution into the `generic` bucket.
    #[test]
    fn every_os_string_matches_the_servers_linux_bucket_rule() {
        for pretty_name in ["Ubuntu 24.04.1 LTS", "openSUSE Leap 15.6", "Alpine v3.20", "Rocky 9.4", "Debian GNU/Linux 12"] {
            let reported = ensure_mentions_linux(pretty_name).to_lowercase();
            assert!(
                ["linux", "ubuntu", "debian", "centos", "fedora"].iter().any(|needle| reported.contains(needle)),
                "{reported:?} would fall through to PlatformBucket.Generic"
            );
        }
    }

    #[test]
    fn parse_flatpak_version_takes_the_second_field() {
        assert_eq!(parse_flatpak_version("Flatpak 1.14.4\n").as_deref(), Some("1.14.4"));
        assert_eq!(parse_flatpak_version(""), None);
    }

    #[test]
    fn parse_flatpak_list_reads_tab_separated_columns_including_names_with_spaces() {
        let stdout = "Firefox\torg.mozilla.firefox\t125.0.3\nGNU Image Manipulation Program\torg.gimp.GIMP\t2.10.36\n";
        let mut available = HashMap::new();
        available.insert("org.mozilla.firefox".to_string(), "126.0".to_string());

        let apps = parse_flatpak_list(stdout, &available);

        assert_eq!(apps.len(), 2);
        assert_eq!(apps[0].name, "Firefox");
        assert_eq!(apps[0].version, "125.0.3");
        assert_eq!(apps[0].application_identifier.as_deref(), Some("org.mozilla.firefox"));
        assert_eq!(apps[0].available_version.as_deref(), Some("126.0"));
        assert_eq!(apps[0].package_manager.as_deref(), Some("Flatpak"));
        // A display name containing spaces must survive intact — the reason these columns are
        // split on tabs rather than whitespace.
        assert_eq!(apps[1].name, "GNU Image Manipulation Program");
        assert_eq!(apps[1].available_version, None);
    }

    #[test]
    fn parse_flatpak_list_reports_a_missing_version_as_unknown() {
        let apps = parse_flatpak_list("Some App\torg.example.App\t\n", &HashMap::new());

        assert_eq!(apps.len(), 1);
        assert_eq!(apps[0].version, "unknown");
    }

    #[test]
    fn parse_flatpak_updates_maps_application_ids_to_versions() {
        let updates = parse_flatpak_updates("org.mozilla.firefox\t126.0\norg.gimp.GIMP\t2.10.38\n");

        assert_eq!(updates.get("org.mozilla.firefox").map(String::as_str), Some("126.0"));
        assert_eq!(updates.len(), 2);
    }

    #[test]
    fn parse_snap_version_takes_the_snapd_line_not_the_snap_one() {
        let stdout = "snap    2.62\nsnapd   2.63\nseries  16\nubuntu  24.04\n";

        assert_eq!(parse_snap_version(stdout).as_deref(), Some("2.63"));
    }

    #[test]
    fn parse_snap_table_skips_the_header_row() {
        let stdout = concat!(
            "Name      Version    Rev    Tracking       Publisher   Notes\n",
            "core22    20240408   1380   latest/stable  canonical   base\n",
            "firefox   125.0.3-1  4173   latest/stable  mozilla     -\n",
        );

        let snaps = parse_snap_table(stdout);

        assert_eq!(snaps.len(), 2);
        assert_eq!(snaps.get("firefox").map(String::as_str), Some("125.0.3-1"));
        assert_eq!(snaps.get("Name"), None);
    }

    /// `snap refresh --list` prints a sentence and no table when everything is current — the case
    /// that would otherwise produce a phantom "All/snaps" entry.
    #[test]
    fn parse_snap_table_returns_nothing_when_everything_is_up_to_date() {
        assert!(parse_snap_table("All snaps up to date.\n").is_empty());
    }
}
