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
    bool? OperatingSystemUpdateAvailable,
    string? OperatingSystemLatestVersion,
    int AppUpdatesAvailableCount,
    bool RemovalRequested)
{
    public static HostDto FromEntity(Host host, int appUpdatesAvailableCount = 0) =>
        new(
            host.Id,
            host.Hostname,
            host.SerialNumber,
            host.OperatingSystem,
            host.IpAddress,
            host.Status,
            host.LastSeenUtc,
            host.OperatingSystemUpdateAvailable,
            host.OperatingSystemLatestVersion,
            appUpdatesAvailableCount,
            host.RemovalRequested);
}
