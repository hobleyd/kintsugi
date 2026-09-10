using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

public class Host : BaseEntity
{
    public string Hostname { get; private set; } = default!;
    public string SerialNumber { get; private set; } = default!;
    public string? OperatingSystem { get; private set; }
    public string? IpAddress { get; private set; }
    public HostStatus Status { get; private set; } = HostStatus.Unknown;
    public DateTimeOffset? LastSeenUtc { get; private set; }

    /// <summary>Whether the host's own standard update check (e.g. <c>softwareupdate -l</c> on
    /// macOS) found a pending OS update at last check-in. Null when never reported.</summary>
    public bool? OperatingSystemUpdateAvailable { get; private set; }

    /// <summary>The OS version that check found available, when known. Null if no update is
    /// available, or the reporting agent couldn't determine a version for it.</summary>
    public string? OperatingSystemLatestVersion { get; private set; }

    /// <summary>
    /// The operating system's own machine-readable identifier, when the agent reports one:
    /// os-release's <c>ID</c> on Linux (<c>ubuntu</c>, <c>debian</c>, <c>fedora</c>). Null on
    /// macOS and Windows, where <see cref="OperatingSystem"/> already identifies the product
    /// unambiguously, and null from any agent predating the release that sends it.
    /// </summary>
    /// <remarks>
    /// Reported rather than parsed out of <see cref="OperatingSystem"/> because that string is
    /// <c>PRETTY_NAME</c>, which is prose: "Ubuntu 24.04.1 LTS", "openSUSE Leap 15.6" and
    /// "Alpine v3.20" share no grammar, and the vulnerability assessment needs a stable key to
    /// hang a CPE mapping off. See each agent's <c>system_info::operating_system</c>.
    /// </remarks>
    public string? OperatingSystemId { get; private set; }

    /// <summary>
    /// The operating system's own machine-readable version, when the agent reports one:
    /// os-release's <c>VERSION_ID</c> on Linux (<c>24.04</c>), and the full build with its update
    /// revision on Windows (<c>22631.4317</c>).
    /// </summary>
    /// <remarks>
    /// On Windows this is what makes an OS assessment honest rather than alarming.
    /// <see cref="OperatingSystem"/> carries only the major build — "Windows 11 Pro 23H2 (22631)"
    /// — and NVD's ranges are evaluated against the revision: querying build 22631.4317 returns
    /// 1355 CVEs where 22631.6000 returns 793, so assuming revision zero reports nearly every
    /// Windows CVE ever filed against a fully patched machine. Null from an agent predating the
    /// release that sends it, which <c>OperatingSystemSubject</c> reports as an approximate
    /// assessment rather than silently guessing.
    /// </remarks>
    public string? OperatingSystemVersionId { get; private set; }

    /// <summary>The version of the agent binary that last checked in, as it reports itself
    /// (each agent's <c>RegisterHostRequest.agent_version</c>, from <c>CARGO_PKG_VERSION</c>).
    /// Null until a release that reports one has checked in; older agents omit the field.</summary>
    public string? AgentVersion { get; private set; }

    /// <summary>Set once an admin has asked for this host to be removed — the next check-in's
    /// response tells the agent to uninstall itself completely from the host machine. See
    /// <see cref="RequestRemoval"/> and <see cref="DeletedAtUtc"/>.</summary>
    public bool RemovalRequested { get; private set; }

    /// <summary>Soft-delete marker set the moment removal is requested, not when the agent
    /// eventually confirms it — this is what hides the host from the hosts list immediately. The
    /// row itself is only ever hard-deleted, once that confirmation actually arrives.</summary>
    public DateTimeOffset? DeletedAtUtc { get; private set; }

    private Host()
    {
    }

    public Host(
        string hostname,
        string serialNumber,
        string? operatingSystem = null,
        string? ipAddress = null,
        bool? operatingSystemUpdateAvailable = null,
        string? operatingSystemLatestVersion = null,
        string? agentVersion = null,
        string? operatingSystemId = null,
        string? operatingSystemVersionId = null)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            throw new DomainException("Hostname is required.");
        }

        if (string.IsNullOrWhiteSpace(serialNumber))
        {
            throw new DomainException("Serial number is required.");
        }

        Hostname = hostname;
        SerialNumber = serialNumber;
        OperatingSystem = operatingSystem;
        IpAddress = ipAddress;
        OperatingSystemUpdateAvailable = operatingSystemUpdateAvailable;
        OperatingSystemLatestVersion = operatingSystemLatestVersion;
        AgentVersion = agentVersion;
        OperatingSystemId = operatingSystemId;
        OperatingSystemVersionId = operatingSystemVersionId;
    }

    public void RecordHeartbeat(HostStatus status)
    {
        Status = status;
        LastSeenUtc = DateTimeOffset.UtcNow;
        MarkUpdated();
    }

    /// <summary>
    /// How long a host may go without checking in before it reads as offline. Agents check in
    /// once an hour (see <c>ICheckInLoadBalancer</c>, which spreads those check-ins across the
    /// hour), so two hours means a host has missed one entirely rather than merely run a few
    /// minutes late — a reassigned check-in minute or a slow network must not flicker it offline.
    /// </summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromHours(2);

    /// <summary>
    /// The status to display, as of <paramref name="now"/>. <see cref="Status"/> is what the agent
    /// last reported — every check-in writes <see cref="HostStatus.Online"/>, and nothing ever
    /// writes anything else — so read alone it says a host is online forever after its first
    /// check-in. This ages it: an online host whose <see cref="LastSeenUtc"/> is older than
    /// <see cref="OnlineWindow"/> is <see cref="HostStatus.Offline"/>. Derived at read time rather
    /// than by a background sweep so it is never stale, and pure so it can be pinned by a test.
    /// </summary>
    public HostStatus StatusAt(DateTimeOffset now)
    {
        if (Status != HostStatus.Online)
        {
            return Status;
        }

        return LastSeenUtc is { } lastSeen && now - lastSeen <= OnlineWindow
            ? HostStatus.Online
            : HostStatus.Offline;
    }

    public void Reregister(
        string hostname,
        string? operatingSystem,
        string? ipAddress,
        bool? operatingSystemUpdateAvailable = null,
        string? operatingSystemLatestVersion = null,
        string? agentVersion = null,
        string? operatingSystemId = null,
        string? operatingSystemVersionId = null)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            throw new DomainException("Hostname is required.");
        }

        Hostname = hostname;

        if (operatingSystem is not null)
        {
            OperatingSystem = operatingSystem;
        }

        if (ipAddress is not null)
        {
            IpAddress = ipAddress;
        }

        // Null means "not reported this time" (an agent that can't run the check at all), left
        // as-is; an explicit false (checked, nothing pending) still overwrites a stale true.
        if (operatingSystemUpdateAvailable is not null)
        {
            OperatingSystemUpdateAvailable = operatingSystemUpdateAvailable;
        }

        if (operatingSystemLatestVersion is not null)
        {
            OperatingSystemLatestVersion = operatingSystemLatestVersion;
        }
        else if (operatingSystemUpdateAvailable == false)
        {
            // A definitive "nothing pending" clears any previously reported target version too,
            // rather than leaving e.g. "15.1" displayed once the host is already caught up.
            OperatingSystemLatestVersion = null;
        }

        // Same null-means-not-reported rule: an older agent that never sends the field must not
        // erase the version a newer build reported before a downgrade or reinstall.
        if (agentVersion is not null)
        {
            AgentVersion = agentVersion;
        }

        // Same null-means-not-reported rule again, and it matters most here: these two arrive only
        // from agents new enough to send them, and blanking them on a mixed-version fleet would
        // downgrade a host's OS assessment back to approximate every time an older sibling binary
        // happened to check in from the same machine.
        if (operatingSystemId is not null)
        {
            OperatingSystemId = operatingSystemId;
        }

        if (operatingSystemVersionId is not null)
        {
            OperatingSystemVersionId = operatingSystemVersionId;
        }

        RecordHeartbeat(HostStatus.Online);
    }

    /// <summary>
    /// Records that a pending macOS update was just successfully installed — clears the pending
    /// flag and target version immediately, the same way a definitive "nothing pending" from
    /// <see cref="Reregister"/> does, rather than leaving them stale until this host's next
    /// check-in re-derives them from a fresh <c>softwareupdate -l</c> run.
    /// </summary>
    public void RecordOperatingSystemPatched()
    {
        OperatingSystemUpdateAvailable = false;
        OperatingSystemLatestVersion = null;
        RecordHeartbeat(HostStatus.Online);
    }

    /// <summary>
    /// An admin has asked for this host to be removed: hides it from the hosts list immediately
    /// (<see cref="DeletedAtUtc"/>) and flags it so the next check-in's response instructs the
    /// agent to uninstall itself completely — see <see cref="RemovalRequested"/>. Idempotent, so
    /// clicking remove twice (or a slow double-submit) is harmless.
    /// </summary>
    public void RequestRemoval()
    {
        if (DeletedAtUtc is not null)
        {
            return;
        }

        RemovalRequested = true;
        DeletedAtUtc = DateTimeOffset.UtcNow;
        MarkUpdated();
    }
}
