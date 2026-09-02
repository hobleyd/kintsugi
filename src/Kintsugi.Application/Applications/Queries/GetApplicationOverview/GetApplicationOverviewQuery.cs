using MediatR;
using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Application.Applications.Queries.GetApplicationOverview;

/// <summary>
/// Everything the Applications screen renders, in one call: each reported application joined with
/// whatever upgrade paths have been researched for it.
/// </summary>
/// <remarks>
/// The join used to live in <c>Pages/Applications.cshtml.cs</c>, which dispatched
/// <see cref="GetApplicationSummaries.GetApplicationSummariesQuery"/> and
/// <see cref="UpgradePaths.Queries.GetUpgradePathSummaries.GetUpgradePathSummariesQuery"/> and
/// stitched them together in the page model. With the UI a Flutter client rather than a server-
/// rendered page there is nowhere for that to live but here — and it belongs here anyway: the
/// pairing rule (match on application *name*, one row per platform) is application logic, not
/// presentation, and having it server-side means the client makes one request instead of two and
/// cannot pair them differently.
/// </remarks>
public record GetApplicationOverviewQuery : IRequest<ApplicationOverviewDto>;

/// <param name="Applications">Top-level applications, each carrying its package-manager-managed
/// children (e.g. Homebrew casks) nested underneath rather than listed beside them.</param>
/// <param name="TotalApplicationCount">Every distinct application reported, children included —
/// which is why it is not <c>Applications.Count</c>. See the note on
/// <c>ApplicationRowDto.Children</c>.</param>
/// <param name="AllHostNames">Every host reporting any application, for the screen's "filter by
/// host" control. Sourced from these same summaries rather than a separate hosts query, so the
/// filter can never offer a host the table has no rows for.</param>
public record ApplicationOverviewDto(
    IReadOnlyList<ApplicationRowDto> Applications,
    int TotalApplicationCount,
    IReadOnlyList<string> AllHostNames);

/// <summary>
/// An application summary joined with the upgrade paths researched for it — one per platform it is
/// installed on.
/// </summary>
public record ApplicationRowDto(
    string Name,
    int HostCount,
    IReadOnlyList<string> HostNames,
    IReadOnlyList<UpgradePathSummaryDto> UpgradePaths,
    IReadOnlyList<ApplicationRowDto> Children);
