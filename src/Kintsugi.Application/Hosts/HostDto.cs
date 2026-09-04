using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Hosts;

public record HostDto(
    Guid Id,
    string Hostname,
    string SerialNumber,
    string? OperatingSystem,
    string? IpAddress,
    HostStatus Status,
    DateTimeOffset? LastSeenUtc,
    string? AgentVersion,
    bool? OperatingSystemUpdateAvailable,
    string? OperatingSystemLatestVersion,
    int AppUpdatesAvailableCount,
    bool RemovalRequested)
{
    /// <summary>
    /// <c>Status</c> is <see cref="Host.StatusAt"/> as of now, not the stored column: the column
    /// only ever records that the host was online at its last check-in, and the Hosts screen
    /// needs to know whether it still is.
    /// </summary>
    public static HostDto FromEntity(Host host, int appUpdatesAvailableCount = 0) =>
        new(
            host.Id,
            host.Hostname,
            host.SerialNumber,
            host.OperatingSystem,
            host.IpAddress,
            host.StatusAt(DateTimeOffset.UtcNow),
            host.LastSeenUtc,
            host.AgentVersion,
            host.OperatingSystemUpdateAvailable,
            host.OperatingSystemLatestVersion,
            appUpdatesAvailableCount,
            host.RemovalRequested);
}
