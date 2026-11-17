using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Baselines.Queries.GetBaseline;

public class GetBaselineQueryHandler : IRequestHandler<GetBaselineQuery, BaselineDetailDto>
{
    private readonly IBaselineRepository _baselines;
    private readonly IHostRepository _hosts;

    public GetBaselineQueryHandler(IBaselineRepository baselines, IHostRepository hosts)
    {
        _baselines = baselines;
        _hosts = hosts;
    }

    public async Task<BaselineDetailDto> Handle(GetBaselineQuery request, CancellationToken cancellationToken)
    {
        var baseline = await _baselines.GetAsync(request.BaselineId, cancellationToken)
            ?? throw new NotFoundException($"No baseline with id '{request.BaselineId}'.");

        var versions = await _baselines.GetVersionsAsync(new[] { baseline.Id }, cancellationToken);
        var hosts = await _hosts.GetAllAsync(cancellationToken);
        var applicable = BaselineCompliance.ApplicableHosts(baseline, hosts);
        var served = BaselineCompliance.ServedVersion(versions);

        var items = served is null ? Array.Empty<BaselineItem>() : BaselineDocument.Parse(served.Document).Items;
        var states = served is null
            ? Array.Empty<Domain.Entities.HostBaselineItemState>()
            : await _baselines.GetStatesAsync(new[] { served.Id }, cancellationToken);

        return new BaselineDetailDto(
            baseline.Id,
            baseline.Name,
            baseline.AssignedToAllWindowsHosts,
            hosts.Where(h => baseline.AssignedHostIds.Contains(h.Id)).Select(h => h.Hostname).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            versions
                .OrderByDescending(v => v.Number)
                .Select(v => new BaselineVersionDto(
                    v.Id, v.Number, v.Document, v.DocumentSha256, v.IsSigned, v.SignedBy, v.SignedUtc, v.CreatedBy, v.CreatedAtUtc))
                .ToList(),
            BaselineCompliance.PerItem(items, applicable.Select(h => h.Id).ToList(), states),
            BaselineCompliance.PerHost(items, applicable, states));
    }
}
