namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Fetches CISA's Known Exploited Vulnerabilities catalog — the list of CVEs observed being
/// exploited in the wild.
/// </summary>
/// <remarks>
/// <para>
/// One anonymous GET of a single JSON document (roughly 1.7 MB, ~1700 entries, refreshed most
/// weekdays), so there is no paging, no key and no rate limit to respect. That makes it the cheap
/// half of this feature and the half worth running first: it needs no CPE mapping at all.
/// </para>
/// <para>
/// <b>A KEV entry cannot tell you whether you are affected.</b> Each carries a CVE id, a vendor, a
/// product and a due date — and no version ranges whatsoever. So this is only ever an overlay on
/// matches NVD's ranges already produced. Going the other way, scanning the inventory for KEV's
/// product names, would flag every host running any version of a named product including a fully
/// patched one.
/// </para>
/// </remarks>
public interface IKevCatalogClient
{
    /// <summary>
    /// Downloads the current catalog. Throws on any failure — a caller must not treat "the fetch
    /// failed" as "the catalog is empty", because the difference decides whether a CVE's exploited
    /// flag is withdrawn or left standing.
    /// </summary>
    Task<KevCatalog> GetCatalogAsync(CancellationToken cancellationToken);
}

/// <param name="CatalogVersion">CISA's own version stamp, e.g. <c>2026.09.09</c>. Reported to the
/// settings screen so an administrator can see how fresh what they are looking at is.</param>
public record KevCatalog(string? CatalogVersion, DateTimeOffset? ReleasedUtc, IReadOnlyList<KevEntry> Entries);

/// <param name="KnownRansomwareUse">CISA records this as the string "Known" or "Unknown"; it is
/// parsed to a bool here so no consumer has to know that.</param>
public record KevEntry(
    string CveId,
    string? VendorProject,
    string? Product,
    string? VulnerabilityName,
    DateTimeOffset? DateAddedUtc,
    DateTimeOffset? DueDateUtc,
    bool KnownRansomwareUse,
    string? ShortDescription,
    string? RequiredAction);
