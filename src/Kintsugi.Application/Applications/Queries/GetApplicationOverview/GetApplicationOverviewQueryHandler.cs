using MediatR;
using Kintsugi.Application.Applications.Queries.GetApplicationSummaries;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathSummaries;

namespace Kintsugi.Application.Applications.Queries.GetApplicationOverview;

public class GetApplicationOverviewQueryHandler : IRequestHandler<GetApplicationOverviewQuery, ApplicationOverviewDto>
{
    private readonly ISender _sender;

    public GetApplicationOverviewQueryHandler(ISender sender)
    {
        _sender = sender;
    }

    public async Task<ApplicationOverviewDto> Handle(GetApplicationOverviewQuery request, CancellationToken cancellationToken)
    {
        var applications = await _sender.Send(new GetApplicationSummariesQuery(), cancellationToken);
        var upgradePaths = await _sender.Send(new GetUpgradePathSummariesQuery(), cancellationToken);

        var upgradePathsByApplicationName = upgradePaths.ToLookup(p => p.ApplicationName, StringComparer.OrdinalIgnoreCase);

        var rows = applications.Select(a => ToRow(a, upgradePathsByApplicationName)).ToList();

        return new ApplicationOverviewDto(
            rows,
            // Package-manager-managed applications are nested under their manager rather than
            // listed at the top level, so the count has to include them explicitly to reflect
            // every distinct application reported.
            rows.Count + rows.Sum(r => r.Children.Count),
            applications
                .SelectMany(FlattenHostNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    private static IEnumerable<string> FlattenHostNames(ApplicationSummaryDto application) =>
        application.HostNames.Concat(application.Children.SelectMany(FlattenHostNames));

    private static ApplicationRowDto ToRow(
        ApplicationSummaryDto application,
        ILookup<string, UpgradePathSummaryDto> upgradePathsByApplicationName) =>
        new(
            application.Name,
            application.HostCount,
            application.HostNames,
            upgradePathsByApplicationName[application.Name].OrderBy(p => p.Platform, StringComparer.OrdinalIgnoreCase).ToList(),
            application.Children.Select(c => ToRow(c, upgradePathsByApplicationName)).ToList());
}
