using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.DeviceHealth;

namespace Kintsugi.Application.DeviceReport;

/// <param name="Status">"Healthy", "Watch" or "Unstable", by the same rule as the Device health panel.</param>
/// <param name="Why">The reason in words; empty for a healthy device.</param>
public record DeviceSearchResultDto(Guid HostId, string Hostname, string? OperatingSystem, string Platform, string Status, string Why, DateTimeOffset? LastEventUtc);

/// <summary>Finds devices by name or serial number, so the report can be opened for a device nobody has flagged yet
/// (a person says their laptop is failing before the health rule has seen enough). Capped, so a fleet of 1,200 returns
/// ten rows, never a list.</summary>
public record SearchDevicesQuery(string Text, int Take) : IRequest<IReadOnlyList<DeviceSearchResultDto>>;

public class SearchDevicesQueryHandler : IRequestHandler<SearchDevicesQuery, IReadOnlyList<DeviceSearchResultDto>>
{
    public const int MaxTake = 20;

    private readonly IHostRepository _hosts;
    private readonly IHostEventRepository _events;

    public SearchDevicesQueryHandler(IHostRepository hosts, IHostEventRepository events)
    {
        _hosts = hosts;
        _events = events;
    }

    public async Task<IReadOnlyList<DeviceSearchResultDto>> Handle(SearchDevicesQuery request, CancellationToken cancellationToken)
    {
        var text = (request.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return [];
        }

        // GetAllAsync already leaves out a host whose removal has been requested.
        var found = (await _hosts.GetAllAsync(cancellationToken))
            .Where(h => h.Hostname.Contains(text, StringComparison.OrdinalIgnoreCase) || h.SerialNumber.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Hostname.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(h => h.Hostname, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(request.Take, 1, MaxTake))
            .ToList();
        if (found.Count == 0)
        {
            return [];
        }

        // One read of the 30-day health events, assessed per result, so a row says the same thing the Device
        // health panel would: what the page shows is never a guess.
        var now = DateTimeOffset.UtcNow;
        var events = await _events.GetHealthEventsSinceAsync(now.AddDays(-30), cancellationToken);
        return found
            .Select(h =>
            {
                var a = HostHealthAssessor.Assess(h.Id, events, now);
                return new DeviceSearchResultDto(h.Id, h.Hostname, h.OperatingSystem, DevicePlatform.Of(h.OperatingSystem), a.Health.ToString(), a.Why, a.LastEventUtc);
            })
            .ToList();
    }
}
