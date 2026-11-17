using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Baselines.Queries.GetBaselines;

public class GetBaselinesQueryHandler : IRequestHandler<GetBaselinesQuery, IReadOnlyList<BaselineSummaryDto>>
{
    private readonly IBaselineRepository _baselines;
    private readonly IHostRepository _hosts;

    public GetBaselinesQueryHandler(IBaselineRepository baselines, IHostRepository hosts)
    {
        _baselines = baselines;
        _hosts = hosts;
    }

    public async Task<IReadOnlyList<BaselineSummaryDto>> Handle(GetBaselinesQuery request, CancellationToken cancellationToken)
    {
        var baselines = await _baselines.GetAllAsync(cancellationToken);
        if (baselines.Count == 0)
        {
            return Array.Empty<BaselineSummaryDto>();
        }

        var versions = await _baselines.GetVersionsAsync(baselines.Select(b => b.Id).ToList(), cancellationToken);
        var hosts = await _hosts.GetAllAsync(cancellationToken);
        var served = baselines
            .Select(b => (Baseline: b, Version: BaselineCompliance.ServedVersion(versions.Where(v => v.BaselineId == b.Id))))
            .ToList();
        var states = await _baselines.GetStatesAsync(
            served.Where(s => s.Version is not null).Select(s => s.Version!.Id).ToList(), cancellationToken);

        return served
            .Select(s =>
            {
                var applicable = BaselineCompliance.ApplicableHosts(s.Baseline, hosts);
                var items = s.Version is null ? Array.Empty<BaselineItem>() : BaselineDocument.Parse(s.Version.Document).Items;
                var perItem = BaselineCompliance.PerItem(
                    items, applicable.Select(h => h.Id).ToList(), states.Where(st => st.BaselineVersionId == s.Version?.Id));

                return new BaselineSummaryDto(
                    s.Baseline.Id,
                    s.Baseline.Name,
                    versions.Where(v => v.BaselineId == s.Baseline.Id).Select(v => v.Number).DefaultIfEmpty(0).Max(),
                    s.Version?.Number,
                    s.Baseline.AssignedToAllWindowsHosts,
                    applicable.Count,
                    items.Count(i => i.IsSetting),
                    BaselineCompliance.Sum(perItem.Where(i => i.Item.IsSetting).Select(i => i.Counts)));
            })
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
