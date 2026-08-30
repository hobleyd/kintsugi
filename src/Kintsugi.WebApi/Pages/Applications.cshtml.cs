using MediatR;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Kintsugi.Application.Applications;
using Kintsugi.Application.Applications.Queries.GetApplicationSummaries;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathSummaries;

namespace Kintsugi.WebApi.Pages;

public class ApplicationsModel : PageModel
{
    private readonly ISender _sender;

    public ApplicationsModel(ISender sender)
    {
        _sender = sender;
    }

    public IReadOnlyList<ApplicationRowViewModel> Applications { get; private set; } = Array.Empty<ApplicationRowViewModel>();

    // Package-manager-managed apps (e.g. Homebrew casks) are nested under their manager in
    // Applications rather than listed at the top level, so the subtitle count needs to include
    // them explicitly to reflect every distinct application reported, not just top-level ones.
    public int TotalApplicationCount { get; private set; }

    // Every distinct host reporting any application, for the "filter by host" dropdown — sourced
    // from the same summaries rather than a separate hosts query.
    public IReadOnlyList<string> AllHostNames { get; private set; } = Array.Empty<string>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var applications = await _sender.Send(new GetApplicationSummariesQuery(), cancellationToken);
        var upgradePaths = await _sender.Send(new GetUpgradePathSummariesQuery(), cancellationToken);

        var upgradePathsByAppName = upgradePaths.ToLookup(p => p.ApplicationName, StringComparer.OrdinalIgnoreCase);

        Applications = applications.Select(a => ToRow(a, upgradePathsByAppName)).ToList();
        TotalApplicationCount = Applications.Count + Applications.Sum(a => a.Children.Count);
        AllHostNames = applications
            .SelectMany(FlattenHostNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> FlattenHostNames(ApplicationSummaryDto application) =>
        application.HostNames.Concat(application.Children.SelectMany(FlattenHostNames));

    private static ApplicationRowViewModel ToRow(
        ApplicationSummaryDto application,
        ILookup<string, UpgradePathSummaryDto> upgradePathsByAppName) =>
        new(
            application.Name,
            application.HostCount,
            application.HostNames,
            upgradePathsByAppName[application.Name].OrderBy(p => p.Platform, StringComparer.OrdinalIgnoreCase).ToList(),
            application.Children.Select(c => ToRow(c, upgradePathsByAppName)).ToList());
}

/// <summary>
/// An <see cref="ApplicationSummaryDto"/> joined with whatever upgrade paths have been researched
/// for it (one per platform it's installed on) — so the Applications table can show upgrade
/// status inline instead of in a separate table.
/// </summary>
public record ApplicationRowViewModel(
    string Name,
    int HostCount,
    IReadOnlyList<string> HostNames,
    IReadOnlyList<UpgradePathSummaryDto> UpgradePaths,
    IReadOnlyList<ApplicationRowViewModel> Children);
