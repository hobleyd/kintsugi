using Kintsugi.Application.AgentPackages;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Hosts;

/// <summary>The newest agent published for one platform, and when it was published here. A plain
/// pair rather than the entity, so the rule can be tested with any age.</summary>
public sealed record PublishedAgent(string Version, DateTimeOffset PublishedUtc);

/// <summary>
/// Works out whether a host's agent is behind the newest one published for its platform, and
/// whether it looks stuck there.
/// </summary>
/// <remarks>
/// <para>
/// **Per platform, never across them.** The Windows, macOS and Linux agents are separate builds with
/// their own version numbers (0.12.x, 0.17.x and 0.13.x at the time of writing), so a Linux host on
/// 0.13.1 is not "behind" a macOS agent on 0.17.3. The package platform is the agent-package
/// namespace (<c>macos</c>, <c>windows</c>, <c>linux</c>), which is deliberately not
/// <see cref="PlatformBucket"/>'s; <see cref="AgentPlatform"/> is the one place that converts.
/// </para>
/// <para>
/// **"Not updating" is derived from what is already known**, not tracked: the package's own
/// <c>CreatedAtUtc</c> (as <see cref="PublishedAgent.PublishedUtc"/>) says when it was published here, and the host's status says whether it has
/// been checking in. Agents look for an update at every hourly check-in (<c>needs_update</c> in each
/// agent's <c>self_update.rs</c>), so a host that has been online for a day with a newer package
/// available has had a full day of chances. The cause is usually the update itself failing — the
/// restart the new binary needs never happening, say — which is why it is worth saying out loud.
/// </para>
/// </remarks>
public static class AgentVersionDrift
{
    /// <summary>How long a newer package can be out, with the host online, before it is called stuck.</summary>
    public static readonly TimeSpan NotUpdatingAfter = TimeSpan.FromHours(24);

    /// <summary>The agent-package platform a host's operating system runs, or null for one no agent
    /// exists for.</summary>
    public static string? AgentPlatform(string? operatingSystem) => PlatformBucket.From(operatingSystem) switch
    {
        PlatformBucket.MacOs => "macos",
        PlatformBucket.Windows => "windows",
        PlatformBucket.Linux => "linux",
        _ => null
    };

    public static (AgentUpdateState State, string? LatestVersion) Evaluate(
        Host host,
        IReadOnlyDictionary<string, PublishedAgent> latestByPlatform,
        DateTimeOffset now)
    {
        var platform = AgentPlatform(host.OperatingSystem);
        if (platform is null || !latestByPlatform.TryGetValue(platform, out var latest))
        {
            return (AgentUpdateState.Unknown, null);
        }

        if (string.IsNullOrWhiteSpace(host.AgentVersion))
        {
            return (AgentUpdateState.Unknown, latest.Version);
        }

        if (!AgentPackageVersion.IsNewer(latest.Version, host.AgentVersion))
        {
            return (AgentUpdateState.Current, latest.Version);
        }

        var stuck = host.StatusAt(now) == HostStatus.Online && now - latest.PublishedUtc >= NotUpdatingAfter;
        return (stuck ? AgentUpdateState.NotUpdating : AgentUpdateState.Behind, latest.Version);
    }
}
