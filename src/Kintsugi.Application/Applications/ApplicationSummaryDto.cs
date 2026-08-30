namespace Kintsugi.Application.Applications;

/// <summary>
/// An application name with a count of hosts reporting it installed, and the names of those
/// hosts (for filtering by host). Package managers (e.g. "Homebrew") carry the apps they manage
/// in <see cref="Children"/>; everything else has an empty children list.
/// </summary>
public record ApplicationSummaryDto(
    string Name,
    int HostCount,
    IReadOnlyList<string> HostNames,
    IReadOnlyList<ApplicationSummaryDto> Children);
