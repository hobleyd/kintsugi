using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Baselines.Queries.GetBaselineHost;

public class GetBaselineHostQueryHandler : IRequestHandler<GetBaselineHostQuery, BaselineHostDetailDto>
{
    private readonly IBaselineRepository _baselines;
    private readonly IHostRepository _hosts;

    public GetBaselineHostQueryHandler(IBaselineRepository baselines, IHostRepository hosts)
    {
        _baselines = baselines;
        _hosts = hosts;
    }

    public async Task<BaselineHostDetailDto> Handle(GetBaselineHostQuery request, CancellationToken cancellationToken)
    {
        var baseline = await _baselines.GetAsync(request.BaselineId, cancellationToken)
            ?? throw new NotFoundException($"No baseline with id '{request.BaselineId}'.");
        var host = await _hosts.GetByIdAsync(request.HostId, cancellationToken)
            ?? throw new NotFoundException($"No host with id '{request.HostId}'.");

        var served = BaselineCompliance.ServedVersion(await _baselines.GetVersionsAsync(new[] { baseline.Id }, cancellationToken));
        if (served is null)
        {
            return new BaselineHostDetailDto(host.Id, host.Hostname, null, Array.Empty<HostItemStateDto>(), Array.Empty<HostFactDto>());
        }

        var items = BaselineDocument.Parse(served.Document).Items;
        var states = (await _baselines.GetHostStatesAsync(host.Id, new[] { served.Id }, cancellationToken))
            .ToDictionary(s => s.ItemKey, StringComparer.Ordinal);
        var factKeys = items.Where(i => i.IsFact).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var facts = await _baselines.GetHostFactsAsync(host.Id, cancellationToken);

        return new BaselineHostDetailDto(
            host.Id,
            host.Hostname,
            served.Number,
            items
                .Where(i => i.IsSetting)
                .Select(i => states.TryGetValue(i.Key, out var s)
                    ? new HostItemStateDto(i, s.State, s.ObservedValue, s.Detail, s.LastCheckedUtc, s.LastChangedUtc)
                    : new HostItemStateDto(i, null, null, null, null, null))
                .ToList(),
            facts
                .Where(f => factKeys.Contains(f.Key))
                .OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => new HostFactDto(f.Key, f.Value, f.LastCheckedUtc, f.LastChangedUtc))
                .ToList());
    }
}
