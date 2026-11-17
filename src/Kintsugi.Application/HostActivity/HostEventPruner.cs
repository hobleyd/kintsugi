using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.HostActivity;

/// <summary>Deletes events older than the configured retention.</summary>
public class HostEventPruner
{
    private readonly IHostEventRepository _events;
    private readonly IHostActivitySettingsRepository _settings;

    public HostEventPruner(IHostEventRepository events, IHostActivitySettingsRepository settings)
    {
        _events = events;
        _settings = settings;
    }

    public async Task<int> PruneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var months = (await _settings.GetAsync(cancellationToken))?.RetentionMonths ?? HostActivitySettings.DefaultRetentionMonths;
        return await _events.DeleteOlderThanAsync(now.AddMonths(-months), cancellationToken);
    }
}
