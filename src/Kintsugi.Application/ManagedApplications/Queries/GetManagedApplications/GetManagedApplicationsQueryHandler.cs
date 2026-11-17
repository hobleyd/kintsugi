using MediatR;
using Kintsugi.Application.Applications;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.ManagedApplications.Queries.GetManagedApplications;

public class GetManagedApplicationsQueryHandler : IRequestHandler<GetManagedApplicationsQuery, ManagedApplicationsDto>
{
    private readonly IManagedApplicationRepository _managed;
    private readonly IInstalledApplicationRepository _installed;

    public GetManagedApplicationsQueryHandler(IManagedApplicationRepository managed, IInstalledApplicationRepository installed)
    {
        _managed = managed;
        _installed = installed;
    }

    public async Task<ManagedApplicationsDto> Handle(GetManagedApplicationsQuery request, CancellationToken cancellationToken)
    {
        var applications = await _managed.GetAllAsync(cancellationToken);
        var owners = await _managed.GetOwnersAsync(cancellationToken);
        var hostCounts = HostCountsByName(await _installed.GetSummariesAsync(cancellationToken));

        return new ManagedApplicationsDto(
            applications
                .Select(a => new ManagedApplicationDto(a.ApplicationName, a.Owner, hostCounts.GetValueOrDefault(a.ApplicationName)))
                .OrderBy(a => a.Owner, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.ApplicationName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            owners.Select(o => o.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>How many hosts report each application, by name without regard to case, across the
    /// top-level rows and the ones nested under a package manager.</summary>
    private static Dictionary<string, int> HostCountsByName(IReadOnlyList<ApplicationSummaryDto> summaries)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Add(ApplicationSummaryDto summary)
        {
            counts[summary.Name] = counts.GetValueOrDefault(summary.Name) + summary.HostCount;
            foreach (var child in summary.Children)
            {
                Add(child);
            }
        }

        foreach (var summary in summaries)
        {
            Add(summary);
        }

        return counts;
    }
}
