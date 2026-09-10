using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.Vulnerabilities;

/// <inheritdoc cref="INvdClient" />
/// <remarks>
/// <para>
/// Two endpoints of the NVD 2.0 API: <c>/rest/json/cves/2.0</c> for the CVEs affecting a CPE name,
/// and <c>/rest/json/cpes/2.0</c> for the dictionary that says whether a vendor and product exist.
/// Both answer anonymously; an API key only raises the rate.
/// </para>
/// <para>
/// <b>The rate limiter is a process-wide singleton and has to be.</b> NVD counts requests per
/// source address across a rolling 30-second window, so pacing held per scope — or per typed
/// client instance — would let the assessment loop and a reviewer's dictionary search each spend
/// the full allowance and collect 403s. See <see cref="NvdRateLimiter"/>, registered as a
/// singleton beside this client for the same reason <c>VantaAccessTokenProvider</c> is.
/// </para>
/// </remarks>
public class NvdClient : INvdClient
{
    private const string CveEndpoint = "https://services.nvd.nist.gov/rest/json/cves/2.0";
    private const string CpeEndpoint = "https://services.nvd.nist.gov/rest/json/cpes/2.0";

    /// <summary>NVD's documented ceiling for the CVE API. A page this size means even a very
    /// out-of-date application is usually one request, and never many.</summary>
    private const int CvePageSize = 2000;

    /// <summary>
    /// A hard stop on paging, well above anything a real product produces (the worst case measured
    /// against the live API was 3337 CVEs for Firefox at *any* version, which is not a query this
    /// makes). It exists so a malformed <c>totalResults</c> cannot spin forever, and reaching it is
    /// logged as a warning rather than passing silently — a truncated finding list that presents
    /// as complete is the failure this codebase refuses elsewhere.
    /// </summary>
    private const int MaxCvePages = 20;

    private readonly HttpClient _httpClient;
    private readonly NvdRateLimiter _rateLimiter;
    private readonly ILogger<NvdClient> _logger;

    public NvdClient(HttpClient httpClient, NvdRateLimiter rateLimiter, ILogger<NvdClient> logger)
    {
        _httpClient = httpClient;
        // NVD is habitually slow under load and answers a wide query in tens of seconds.
        _httpClient.Timeout = TimeSpan.FromSeconds(120);
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NvdCveRecord>> GetCvesForCpeAsync(string cpeName, string? apiKey, CancellationToken cancellationToken)
    {
        var records = new List<NvdCveRecord>();
        var startIndex = 0;

        for (var page = 0; page < MaxCvePages; page++)
        {
            var url = $"{CveEndpoint}?virtualMatchString={Uri.EscapeDataString(cpeName)}&resultsPerPage={CvePageSize}&startIndex={startIndex}";
            var response = await GetAsync<CveResponse>(url, apiKey, cancellationToken);

            if (response?.Vulnerabilities is null)
            {
                break;
            }

            foreach (var item in response.Vulnerabilities)
            {
                var record = ToRecord(item.Cve);
                if (record is not null)
                {
                    records.Add(record);
                }
            }

            startIndex += Math.Max(response.ResultsPerPage, response.Vulnerabilities.Count);

            // Paging is not optional here. A three-year-old browser genuinely exceeds one page, and
            // stopping at the first would drop findings for precisely the most out-of-date installs
            // — the ones this whole feature exists to surface.
            if (startIndex >= response.TotalResults || response.Vulnerabilities.Count == 0)
            {
                return records;
            }
        }

        _logger.LogWarning(
            "Stopped paging NVD for {Cpe} after {Pages} pages ({Count} CVEs); the result list may be incomplete",
            cpeName, MaxCvePages, records.Count);

        return records;
    }

    public async Task<bool> CpeExistsAsync(string cpeMatchString, string? apiKey, CancellationToken cancellationToken)
    {
        var url = $"{CpeEndpoint}?cpeMatchString={Uri.EscapeDataString(cpeMatchString)}&resultsPerPage=1";
        var response = await GetAsync<CpeResponse>(url, apiKey, cancellationToken);
        return response is { TotalResults: > 0 };
    }

    public async Task<IReadOnlyList<CpeCandidate>> SearchCpeDictionaryAsync(string keyword, string? apiKey, CancellationToken cancellationToken)
    {
        // A single page is enough to rank candidates: the dictionary holds one entry per released
        // version, so a well-known product contributes hundreds of entries that all collapse to the
        // same triple, and anything not represented in the first page was never a serious answer.
        var url = $"{CpeEndpoint}?keywordSearch={Uri.EscapeDataString(keyword)}&resultsPerPage=500";
        var response = await GetAsync<CpeResponse>(url, apiKey, cancellationToken);

        if (response?.Products is null)
        {
            return Array.Empty<CpeCandidate>();
        }

        var candidates = new Dictionary<string, (string Part, string Vendor, string Product, string? Title, int Count)>();

        foreach (var product in response.Products)
        {
            var name = product.Cpe?.CpeName;
            if (string.IsNullOrWhiteSpace(name) || product.Cpe!.Deprecated)
            {
                // A deprecated entry names a product NVD has superseded; offering it to a reviewer
                // would map the fleet onto a CPE that new CVEs are no longer filed against.
                continue;
            }

            var parts = SplitCpe(name);
            if (parts is null)
            {
                continue;
            }

            var (part, vendor, prod) = parts.Value;
            var key = $"{part}:{vendor}:{prod}";
            var title = product.Cpe.Titles?.FirstOrDefault(t => t.Lang == "en")?.Title;

            if (candidates.TryGetValue(key, out var existing))
            {
                candidates[key] = (part, vendor, prod, existing.Title ?? title, existing.Count + 1);
            }
            else
            {
                candidates[key] = (part, vendor, prod, title, 1);
            }
        }

        return candidates.Values
            .OrderByDescending(c => c.Count)
            .Select(c => new CpeCandidate(c.Part, c.Vendor, c.Product, c.Title, c.Count))
            .ToList();
    }

    private async Task<T?> GetAsync<T>(string url, string? apiKey, CancellationToken cancellationToken)
    {
        var hasKey = !string.IsNullOrWhiteSpace(apiKey);
        await _rateLimiter.WaitAsync(hasKey, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (hasKey)
        {
            // Attached per request rather than to DefaultRequestHeaders — the same rule the GitHub
            // and Vanta clients follow, and for the same reason: a typed client outlives one call,
            // and the key is editable on the settings page while this is running.
            request.Headers.Add("apiKey", apiKey);
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == (HttpStatusCode)429)
            {
                // NVD answers an exceeded rate with 403 rather than 429, which is easy to misread
                // as an authentication failure and is worth naming as what it is.
                throw new ExternalServiceException(
                    "NVD refused the request for exceeding its rate limit. An API key raises the limit from 5 to 50 requests per 30 seconds.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ExternalServiceException($"NVD answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ExternalServiceException($"Could not reach NVD: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Pulls the part, vendor and product out of a CPE 2.3 formatted string, honouring backslash
    /// escapes — a product token legitimately contains an escaped colon, and splitting naively
    /// would read the tail of one component as the head of the next.
    /// </summary>
    private static (string Part, string Vendor, string Product)? SplitCpe(string cpeName)
    {
        var components = new List<string>();
        var current = new System.Text.StringBuilder();
        var escaped = false;

        foreach (var c in cpeName)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
            }
            else if (c == '\\')
            {
                escaped = true;
            }
            else if (c == ':')
            {
                components.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        components.Add(current.ToString());

        // cpe:2.3:<part>:<vendor>:<product>:...
        return components.Count >= 5 ? (components[2], components[3], components[4]) : null;
    }

    private static NvdCveRecord? ToRecord(CveItem? cve)
    {
        if (cve?.Id is null)
        {
            return null;
        }

        var description = cve.Descriptions?.FirstOrDefault(d => d.Lang == "en")?.Value
            ?? cve.Descriptions?.FirstOrDefault()?.Value;

        var metric = SelectMetric(cve.Metrics);

        return new NvdCveRecord(
            cve.Id.Trim().ToUpperInvariant(),
            description,
            metric?.CvssData?.BaseScore,
            metric?.CvssData?.VectorString,
            // v3.x and v4 carry the band inside cvssData; v2 carries it on the enclosing element.
            metric?.CvssData?.BaseSeverity ?? metric?.BaseSeverity,
            metric?.CvssData?.Version,
            ParseTimestamp(cve.Published),
            ParseTimestamp(cve.LastModified));
    }

    /// <summary>
    /// Picks the score to keep. Newest CVSS revision first, because that is the one NVD itself
    /// leads with, and within a revision the "Primary" entry — NVD's own analysis — ahead of a
    /// secondary one supplied by the reporting party.
    /// </summary>
    private static CvssMetric? SelectMetric(CveMetrics? metrics)
    {
        if (metrics is null)
        {
            return null;
        }

        foreach (var candidates in new[] { metrics.CvssMetricV40, metrics.CvssMetricV31, metrics.CvssMetricV30, metrics.CvssMetricV2 })
        {
            if (candidates is null || candidates.Count == 0)
            {
                continue;
            }

            return candidates.FirstOrDefault(m => m.Type == "Primary") ?? candidates[0];
        }

        return null;
    }

    /// <summary>NVD stamps these without a zone offset (e.g. <c>2024-09-17T13:15:04.423</c>) and
    /// documents them as UTC, so they are read as UTC rather than as the server's local time.</summary>
    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private record CveResponse(
        [property: JsonPropertyName("totalResults")] int TotalResults,
        [property: JsonPropertyName("resultsPerPage")] int ResultsPerPage,
        [property: JsonPropertyName("startIndex")] int StartIndex,
        [property: JsonPropertyName("vulnerabilities")] IReadOnlyList<CveWrapper>? Vulnerabilities);

    private record CveWrapper([property: JsonPropertyName("cve")] CveItem? Cve);

    private record CveItem(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("published")] string? Published,
        [property: JsonPropertyName("lastModified")] string? LastModified,
        [property: JsonPropertyName("descriptions")] IReadOnlyList<LocalizedText>? Descriptions,
        [property: JsonPropertyName("metrics")] CveMetrics? Metrics);

    private record LocalizedText(
        [property: JsonPropertyName("lang")] string? Lang,
        [property: JsonPropertyName("value")] string? Value);

    private record CveMetrics(
        [property: JsonPropertyName("cvssMetricV40")] IReadOnlyList<CvssMetric>? CvssMetricV40,
        [property: JsonPropertyName("cvssMetricV31")] IReadOnlyList<CvssMetric>? CvssMetricV31,
        [property: JsonPropertyName("cvssMetricV30")] IReadOnlyList<CvssMetric>? CvssMetricV30,
        [property: JsonPropertyName("cvssMetricV2")] IReadOnlyList<CvssMetric>? CvssMetricV2);

    private record CvssMetric(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("baseSeverity")] string? BaseSeverity,
        [property: JsonPropertyName("cvssData")] CvssData? CvssData);

    private record CvssData(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("vectorString")] string? VectorString,
        [property: JsonPropertyName("baseScore")] double? BaseScore,
        [property: JsonPropertyName("baseSeverity")] string? BaseSeverity);

    private record CpeResponse(
        [property: JsonPropertyName("totalResults")] int TotalResults,
        [property: JsonPropertyName("products")] IReadOnlyList<CpeProduct>? Products);

    private record CpeProduct([property: JsonPropertyName("cpe")] CpeItem? Cpe);

    private record CpeItem(
        [property: JsonPropertyName("cpeName")] string? CpeName,
        [property: JsonPropertyName("deprecated")] bool Deprecated,
        [property: JsonPropertyName("titles")] IReadOnlyList<CpeTitle>? Titles);

    private record CpeTitle(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("lang")] string? Lang);
}
