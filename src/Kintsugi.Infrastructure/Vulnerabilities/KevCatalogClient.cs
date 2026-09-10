using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.Vulnerabilities;

/// <inheritdoc cref="IKevCatalogClient" />
/// <remarks>
/// The catalog is a single public JSON document, so this is the simplest external client in the
/// codebase: no key, no paging, no rate limit. It is also the only vulnerability source that
/// needs no CPE mapping, which is why the assessment run refreshes it first and unconditionally —
/// a deployment that has confirmed no mappings at all still gets a correct exploited-CVE list, it
/// just has nothing to intersect it with yet.
/// </remarks>
public class KevCatalogClient : IKevCatalogClient
{
    /// <summary>
    /// CISA's published feed location. Not configurable, deliberately: an administrator pointing
    /// this at an arbitrary URL is choosing who gets to assert that a CVE is being exploited, and
    /// that assertion is what the Vulnerabilities screen leads with.
    /// </summary>
    public const string CatalogUrl = "https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json";

    private readonly HttpClient _httpClient;
    private readonly ILogger<KevCatalogClient> _logger;

    public KevCatalogClient(HttpClient httpClient, ILogger<KevCatalogClient> logger)
    {
        _httpClient = httpClient;
        // The document is ~1.7 MB and CISA occasionally serves it slowly; well short of the
        // background service's own patience, and this is a once-per-run call.
        _httpClient.Timeout = TimeSpan.FromSeconds(120);
        _logger = logger;
    }

    public async Task<KevCatalog> GetCatalogAsync(CancellationToken cancellationToken)
    {
        KevCatalogResponse? response;
        try
        {
            response = await _httpClient.GetFromJsonAsync<KevCatalogResponse>(CatalogUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Thrown rather than returned empty: the caller withdraws the exploited flag from CVEs
            // the catalog no longer lists, and "the download failed" must never be mistaken for
            // "nothing is exploited any more".
            throw new ExternalServiceException($"Could not download the CISA KEV catalog: {ex.Message}", ex);
        }

        if (response?.Vulnerabilities is null)
        {
            throw new ExternalServiceException("The CISA KEV catalog response contained no vulnerabilities.");
        }

        var entries = new List<KevEntry>(response.Vulnerabilities.Count);
        foreach (var entry in response.Vulnerabilities)
        {
            if (string.IsNullOrWhiteSpace(entry.CveId))
            {
                continue;
            }

            entries.Add(new KevEntry(
                entry.CveId.Trim().ToUpperInvariant(),
                entry.VendorProject,
                entry.Product,
                entry.VulnerabilityName,
                ParseDate(entry.DateAdded),
                ParseDate(entry.DueDate),
                // CISA writes this as the strings "Known" and "Unknown" rather than a boolean, and
                // has also used "Unknown" for entries predating the field.
                string.Equals(entry.KnownRansomwareCampaignUse, "Known", StringComparison.OrdinalIgnoreCase),
                entry.ShortDescription,
                entry.RequiredAction));
        }

        _logger.LogInformation(
            "Downloaded the CISA KEV catalog: {Count} entries, version {Version}", entries.Count, response.CatalogVersion);

        return new KevCatalog(response.CatalogVersion, ParseTimestamp(response.DateReleased), entries);
    }

    /// <summary>Catalog dates are bare <c>yyyy-MM-dd</c> with no zone. Read as UTC so a server west
    /// of Greenwich does not show every due date a day early.</summary>
    private static DateTimeOffset? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : null;

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private record KevCatalogResponse(
        [property: JsonPropertyName("catalogVersion")] string? CatalogVersion,
        [property: JsonPropertyName("dateReleased")] string? DateReleased,
        [property: JsonPropertyName("vulnerabilities")] IReadOnlyList<KevCatalogEntry>? Vulnerabilities);

    private record KevCatalogEntry(
        [property: JsonPropertyName("cveID")] string? CveId,
        [property: JsonPropertyName("vendorProject")] string? VendorProject,
        [property: JsonPropertyName("product")] string? Product,
        [property: JsonPropertyName("vulnerabilityName")] string? VulnerabilityName,
        [property: JsonPropertyName("dateAdded")] string? DateAdded,
        [property: JsonPropertyName("dueDate")] string? DueDate,
        [property: JsonPropertyName("knownRansomwareCampaignUse")] string? KnownRansomwareCampaignUse,
        [property: JsonPropertyName("shortDescription")] string? ShortDescription,
        [property: JsonPropertyName("requiredAction")] string? RequiredAction);
}
