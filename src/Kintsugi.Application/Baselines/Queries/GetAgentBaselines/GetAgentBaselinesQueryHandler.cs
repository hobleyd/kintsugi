using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Baselines.Queries.GetAgentBaselines;

public class GetAgentBaselinesQueryHandler : IRequestHandler<GetAgentBaselinesQuery, AgentBaselinesDto>
{
    private readonly IBaselineRepository _baselines;
    private readonly IHostRepository _hosts;

    public GetAgentBaselinesQueryHandler(IBaselineRepository baselines, IHostRepository hosts)
    {
        _baselines = baselines;
        _hosts = hosts;
    }

    public async Task<AgentBaselinesDto> Handle(GetAgentBaselinesQuery request, CancellationToken cancellationToken)
    {
        var empty = new AgentBaselinesDto(Array.Empty<AgentBaselineDto>());

        // Nothing rather than a 404, like ClaimForcedPatchRunsCommandHandler: an enrolled host that has not
        // registered yet simply has no baseline.
        var host = await _hosts.GetBySerialNumberAsync(request.SerialNumber, cancellationToken);
        if (host is null || host.DeletedAtUtc is not null)
        {
            return empty;
        }

        var assigned = (await _baselines.GetAllAsync(cancellationToken))
            .Where(b => b.AppliesTo(host.Id, BaselineCompliance.IsWindows(host)))
            .ToList();
        if (assigned.Count == 0)
        {
            return empty;
        }

        var served = await _baselines.GetServedVersionsAsync(assigned.Select(b => b.Id).ToList(), cancellationToken);
        if (served.Count == 0)
        {
            return empty;
        }

        var states = await _baselines.GetHostStatesAsync(host.Id, served.Select(v => v.Id).ToList(), cancellationToken);
        var facts = await _baselines.GetHostFactsAsync(host.Id, cancellationToken);

        return new AgentBaselinesDto(served
            .Select(version =>
            {
                var factKeys = BaselineDocument.Parse(version.Document).Items
                    .Where(i => i.IsFact).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
                return new AgentBaselineDto(
                    version.Id,
                    version.Document,
                    version.Signature!,
                    states.Where(s => s.BaselineVersionId == version.Id)
                        .Select(s => new AgentItemStateDto(s.ItemKey, s.State.ToString(), s.ObservedValue))
                        .ToList(),
                    facts.Where(f => factKeys.Contains(f.Key)).Select(f => new AgentFactDto(f.Key, f.Value)).ToList());
            })
            .ToList());
    }
}
