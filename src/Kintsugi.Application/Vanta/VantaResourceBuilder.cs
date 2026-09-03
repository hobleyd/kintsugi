using System.Globalization;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Vanta;

/// <summary>
/// Turns this server's own view of the fleet into the two Vanta resource collections it syncs.
/// Pure: no I/O, no clock, no configuration beyond the snapshot handed in — everything that decides
/// what Vanta ends up holding is decided here and is therefore testable without a Vanta account.
/// </summary>
public static class VantaResourceBuilder
{
    /// <summary>
    /// Vanta's <c>targetType</c> for every component this server syncs.
    /// </summary>
    /// <remarks>
    /// Uniformly <c>HOST</c>, never <c>WORKSTATION</c> or <c>SERVER</c>, and that is a decision
    /// rather than laziness: Kintsugi records an operating system string, not a machine's role.
    /// Mapping macOS and Windows to WORKSTATION and Linux to SERVER would be a guess — the fleet has
    /// Windows servers and Linux desktops in it — and a guess that files evidence under the wrong
    /// category in a compliance tool is worse than the accurate general term.
    /// </remarks>
    public const string HostTargetType = "HOST";

    /// <summary>Prefix on every <c>uniqueId</c> this server mints, so records it owns are
    /// distinguishable in Vanta from another integration's.</summary>
    private const string IdPrefix = "kintsugi";

    /// <summary>
    /// Builds the complete state of the world. Ordering is stable (by serial, then application name)
    /// so two builds over the same fleet state produce identical payloads — which is what makes a
    /// diff of two sync runs meaningful.
    /// </summary>
    /// <param name="hosts">Every host that has not been removed.</param>
    /// <param name="outdatedStatuses">Every (host, application) pairing whose installed version is
    /// behind the latest known one, fleet-wide.</param>
    /// <param name="settings">Resolved configuration — the console address every
    /// <c>externalUrl</c> is built from, and the severity every package record carries.</param>
    public static VantaFleetSnapshot Build(
        IReadOnlyList<Host> hosts,
        IReadOnlyList<UpgradeStatusDto> outdatedStatuses,
        VantaSettingsSnapshot settings)
    {
        var consoleBaseUrl = (settings.ConsoleBaseUrl ?? string.Empty).TrimEnd('/');

        // A host that has never checked in has never had anything collected from it, and
        // collectedTimestamp is required. Stamping "now" on it would tell Vanta this server had just
        // scanned a machine it has never heard from — the same class of error as reporting a drive's
        // encryption status this system does not collect. So it is left out of the sync entirely.
        var syncable = hosts
            .Where(h => h.LastSeenUtc is not null)
            .OrderBy(h => h.SerialNumber, StringComparer.Ordinal)
            .ToList();

        var components = syncable.Select(h => BuildComponent(h, consoleBaseUrl)).ToList();

        var syncedSerials = new HashSet<string>(syncable.Select(h => h.SerialNumber), StringComparer.OrdinalIgnoreCase);
        var packages = new List<VantaPackageVulnerability>();

        foreach (var host in syncable)
        {
            if (host.OperatingSystemUpdateAvailable == true)
            {
                packages.Add(BuildOperatingSystemPackage(host, consoleBaseUrl, settings.Severity));
            }
        }

        foreach (var status in outdatedStatuses
            .Where(s => syncedSerials.Contains(s.SerialNumber))
            .OrderBy(s => s.SerialNumber, StringComparer.Ordinal)
            .ThenBy(s => s.ApplicationName, StringComparer.OrdinalIgnoreCase))
        {
            packages.Add(BuildApplicationPackage(status, consoleBaseUrl, settings.Severity));
        }

        return new VantaFleetSnapshot(components, packages);
    }

    /// <summary>
    /// The component <c>uniqueId</c> for a host: its serial number, and never its database key.
    /// </summary>
    /// <remarks>
    /// The serial is this system's real host identity — it is the certificate CN every authenticated
    /// request is checked against (see <c>RequireAgentIdentityAttribute</c>) and it is the only thing
    /// about a host that never changes; <c>Host.Reregister</c> can change the hostname, and deleting
    /// and re-enrolling a machine gives it a fresh <c>Host.Id</c>. Keying on the serial means the
    /// same physical machine keeps one Vanta record across both, which is what an auditor reading
    /// that record expects.
    /// </remarks>
    public static string ComponentUniqueId(string serialNumber) =>
        $"{IdPrefix}:host:{serialNumber.Trim().ToLowerInvariant()}";

    /// <summary>
    /// The package <c>uniqueId</c> for one application on one host.
    /// </summary>
    /// <remarks>
    /// Derived from (serial, application name) and never from <c>InstalledApplication.Id</c>. That
    /// row is not stable: <c>RegisterApplicationsCommandHandler</c> deletes every previously reported
    /// application for a host and inserts fresh entities on every routine inventory report, so a
    /// row-keyed id would change every check-in — and since each sync is a state-of-the-world
    /// replacement, Vanta would see the entire fleet's vulnerabilities deleted and recreated daily,
    /// losing whatever age or remediation history it tracks against them.
    /// Application names are lowercased for the same reason the repository matches them
    /// case-insensitively: the casing a report settles on can vary run to run.
    /// </remarks>
    public static string ApplicationPackageUniqueId(string serialNumber, string applicationName) =>
        $"{IdPrefix}:app:{serialNumber.Trim().ToLowerInvariant()}:{applicationName.Trim().ToLowerInvariant()}";

    /// <summary>The package <c>uniqueId</c> for a host's pending operating-system update. A separate
    /// namespace from <see cref="ApplicationPackageUniqueId"/> so it cannot collide with an
    /// application that happens to be named "operating system".</summary>
    public static string OperatingSystemPackageUniqueId(string serialNumber) =>
        $"{IdPrefix}:os:{serialNumber.Trim().ToLowerInvariant()}";

    private static VantaVulnerableComponent BuildComponent(Host host, string consoleBaseUrl) =>
        new(
            host.Hostname,
            ComponentUniqueId(host.SerialNumber),
            $"{consoleBaseUrl}/hosts",
            // The moment this host last reported to Kintsugi — not the moment of the sync. A host
            // that stopped checking in three weeks ago must not look freshly scanned.
            host.LastSeenUtc!.Value,
            host.Hostname,
            host.OperatingSystem is null
                ? $"Host managed by Kintsugi patch management (serial {host.SerialNumber})."
                : $"{host.OperatingSystem} host managed by Kintsugi patch management (serial {host.SerialNumber}).",
            HostTargetType);

    private static VantaPackageVulnerability BuildApplicationPackage(
        UpgradeStatusDto status, string consoleBaseUrl, double severity)
    {
        var latest = status.LatestVersion ?? "a newer version";

        return new VantaPackageVulnerability(
            $"{status.ApplicationName} {status.InstalledVersion} on {status.Hostname}",
            ApplicationPackageUniqueId(status.SerialNumber, status.ApplicationName),
            ApplicationsUrl(consoleBaseUrl, status.Hostname),
            status.ApplicationName,
            status.InstalledVersion,
            severity,
            ComponentUniqueId(status.SerialNumber),
            $"{status.ApplicationName} {status.InstalledVersion} is installed on {status.Hostname}; {latest} is the latest known version. "
                + "Reported by Kintsugi from an installed-version comparison, not from a CVE feed.",
            IsResolvable(status),
            RemediationFor(status, latest));
    }

    private static VantaPackageVulnerability BuildOperatingSystemPackage(Host host, string consoleBaseUrl, double severity)
    {
        var installed = host.OperatingSystem ?? "unknown";
        var latest = host.OperatingSystemLatestVersion;
        var latestSentence = latest is null
            ? "The host's own update check reports an update is pending."
            : $"The host's own update check reports {latest} is available.";

        return new VantaPackageVulnerability(
            $"Operating system {installed} on {host.Hostname}",
            OperatingSystemPackageUniqueId(host.SerialNumber),
            $"{consoleBaseUrl}/hosts",
            "Operating system",
            installed,
            severity,
            ComponentUniqueId(host.SerialNumber),
            $"{host.Hostname} is running {installed} and has a pending operating-system update. {latestSentence}",
            // Every agent installs OS updates through the privileged half of itself — softwareupdate
            // on macOS, the Windows Update Agent COM API, apt/dnf/zypper/pacman on Linux — so a
            // pending OS update is always something this system can act on, unlike an application
            // whose upgrade path may not have a reviewed script yet.
            true,
            "Kintsugi installs pending operating system updates on this host at its next patching window.");
    }

    /// <summary>
    /// Whether Kintsugi could actually apply this upgrade unattended.
    /// </summary>
    /// <remarks>
    /// Mirrors the agents' own <c>is_patchable</c> (see <c>clients/*/src/upgrade.rs</c>) so the
    /// record does not tell Vanta a finding is resolvable when no agent would in fact touch it. One
    /// difference is inherent and worth stating: the agent *verifies* the signature against its
    /// pinned key before running anything, whereas this can only see that a signature is recorded.
    /// A row whose signature is present but invalid therefore reports resolvable here and is
    /// refused on the host — which is the safe direction for the disagreement to run, and one an
    /// operator sees on the Upgrade Scripts screen.
    /// </remarks>
    private static bool IsResolvable(UpgradeStatusDto status) => status.Method switch
    {
        UpgradeMethod.PackageManagerCommand => status.Command is not null && status.CommandSignature is not null,
        UpgradeMethod.Script => status.Script is not null
            && status.ApplicationIdentifier is not null
            && status.ScriptSignature is not null,
        _ => false,
    };

    /// <summary>
    /// Vanta requires non-empty remediation text, so this always produces some. The researched
    /// instructions are best; a signed package-manager command is next; the fallback states the
    /// upgrade in plain terms rather than leaving a compliance record saying nothing.
    /// </summary>
    private static string RemediationFor(UpgradeStatusDto status, string latest)
    {
        if (!string.IsNullOrWhiteSpace(status.Instructions))
        {
            return status.Instructions.Trim();
        }

        if (!string.IsNullOrWhiteSpace(status.Command))
        {
            return $"Run: {status.Command.Trim()}";
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "Upgrade {0} from {1} to {2} on {3}.",
            status.ApplicationName,
            status.InstalledVersion,
            latest,
            status.Hostname);
    }

    /// <summary>The Applications screen, filtered to this host's outstanding updates — the same deep
    /// link the Hosts screen's own "N app updates" badge uses (see <c>app_router.dart</c>).</summary>
    private static string ApplicationsUrl(string consoleBaseUrl, string hostname) =>
        $"{consoleBaseUrl}/applications?status={UpgradePathStatusKey.UpdateAvailable}&host={Uri.EscapeDataString(hostname)}";
}
