using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// Reduces an <see cref="UpgradePathSummaryDto"/> down to the same stable status key its status
/// badge is chosen from, so the Applications page can filter/sort by status client-side, and so
/// other pages (e.g. Hosts) can deep-link to a status without duplicating this branching.
/// </summary>
public static class UpgradePathStatusKey
{
    public const string CheckFailed = "check-failed";
    public const string NotFound = "not-found";
    public const string ReviewAndSign = "review-sign";
    public const string UpdateAvailable = "update-available";
    public const string UpToDate = "up-to-date";
    public const string NotChecked = "not-checked";

    public static string For(UpgradePathSummaryDto path)
    {
        if (path.Status == UpgradePathStatus.Failed)
        {
            return CheckFailed;
        }
        if (path.Status == UpgradePathStatus.NotFound)
        {
            return NotFound;
        }
        if (path.Method == UpgradeMethod.Script && path.Script is not null && path.ScriptSignature is null)
        {
            return ReviewAndSign;
        }
        return path.UpdateAvailableHostCount > 0 ? UpdateAvailable : UpToDate;
    }
}
