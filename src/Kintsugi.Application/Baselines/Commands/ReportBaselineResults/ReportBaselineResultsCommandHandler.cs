using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Baselines.Commands.ReportBaselineResults;

public class ReportBaselineResultsCommandHandler : IRequestHandler<ReportBaselineResultsCommand>
{
    private readonly IBaselineRepository _baselines;
    private readonly IHostRepository _hosts;
    private readonly IUnitOfWork _unitOfWork;

    public ReportBaselineResultsCommandHandler(IBaselineRepository baselines, IHostRepository hosts, IUnitOfWork unitOfWork)
    {
        _baselines = baselines;
        _hosts = hosts;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(ReportBaselineResultsCommand request, CancellationToken cancellationToken)
    {
        var host = await _hosts.GetBySerialNumberAsync(request.SerialNumber, cancellationToken);
        if (host is null || host.DeletedAtUtc is not null)
        {
            throw new NotFoundException($"No host is registered with serial number '{request.SerialNumber}'.");
        }

        var now = DateTimeOffset.UtcNow;
        var assigned = (await _baselines.GetAllAsync(cancellationToken))
            .Where(b => b.AppliesTo(host.Id, BaselineCompliance.IsWindows(host)))
            .ToDictionary(b => b.Id);
        var facts = (await _baselines.GetHostFactsAsync(host.Id, cancellationToken)).ToDictionary(f => f.Key, StringComparer.Ordinal);
        var served = (await _baselines.GetServedVersionsAsync(assigned.Keys.ToList(), cancellationToken)).ToDictionary(v => v.Id);

        foreach (var result in request.Baselines)
        {
            // Only the version currently served for a baseline this host is assigned is recorded. Anything
            // else is dropped rather than failing the whole report, which would cost the other baselines'
            // results: a report that raced an unassignment, a version the server never served — or an
            // older signed one, which an agent sends when its fetch failed and it fell back to its cached
            // copy (baseline::refresh). Accepting that one would delete the current version's rows below.
            if (!served.TryGetValue(result.VersionId, out var version))
            {
                continue;
            }

            var items = BaselineDocument.Parse(version.Document).Items;
            var settingKeys = items.Where(i => i.IsSetting).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
            var factKeys = items.Where(i => i.IsFact).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);

            var allVersionIds = await _baselines.GetVersionIdsAsync(version.BaselineId, cancellationToken);
            var existing = await _baselines.GetHostStatesAsync(host.Id, allVersionIds, cancellationToken);

            // A host on a new version has nothing to say about the old one any more.
            _baselines.RemoveStates(existing.Where(s => s.BaselineVersionId != version.Id));

            var current = existing.Where(s => s.BaselineVersionId == version.Id).ToDictionary(s => s.ItemKey, StringComparer.Ordinal);
            foreach (var row in current.Values)
            {
                row.MarkChecked(now);
            }

            foreach (var item in result.Items.Where(i => settingKeys.Contains(i.Key)))
            {
                if (!current.TryGetValue(item.Key, out var row))
                {
                    row = HostBaselineItemState.Create(host.Id, version.Id, item.Key, now);
                    current[item.Key] = row;
                    await _baselines.AddStateAsync(row, cancellationToken);
                }

                row.Record(Enum.Parse<BaselineItemState>(item.State), item.Observed, item.Detail, now);
            }

            foreach (var key in factKeys)
            {
                if (facts.TryGetValue(key, out var known))
                {
                    known.MarkChecked(now);
                }
            }

            foreach (var reported in result.Facts.Where(f => factKeys.Contains(f.Key)))
            {
                if (facts.TryGetValue(reported.Key, out var fact))
                {
                    fact.Record(reported.Value, now);
                }
                else
                {
                    fact = HostFact.Create(host.Id, reported.Key, reported.Value, now);
                    facts[reported.Key] = fact;
                    await _baselines.AddFactAsync(fact, cancellationToken);
                }
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
