using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.Vulnerabilities;

/// <inheritdoc cref="IOsvClient" />
/// <remarks>
/// No API key and no documented rate limit, which makes this the cheap half of the assessment
/// compared with NVD's 5 requests per 30 seconds — one batch call covers hundreds of packages. It
/// is deliberately not put behind <see cref="NvdRateLimiter"/>: that limiter exists for NVD's
/// per-address window, and sharing it would make OSV wait out a limit that does not apply to it.
/// </remarks>
public class OsvClient : IOsvClient
{
    private const string QueryBatchUrl = "https://api.osv.dev/v1/querybatch";
    private const string VulnUrl = "https://api.osv.dev/v1/vulns/";

    /// <summary>
    /// How many packages go in one batch. OSV documents a ceiling of 1000; this is well under it
    /// because the response carries every matching identifier for every query, and a few hundred
    /// out-of-date packages produce a large document.
    /// </summary>
    public const int MaxBatchSize = 200;

    private readonly HttpClient _httpClient;
    private readonly ILogger<OsvClient> _logger;

    public OsvClient(HttpClient httpClient, ILogger<OsvClient> logger)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(120);
        _logger = logger;
    }

    public async Task<IReadOnlyList<OsvQueryResult>> QueryBatchAsync(
        IReadOnlyList<OsvPackageQuery> queries, CancellationToken cancellationToken)
    {
        if (queries.Count == 0)
        {
            return Array.Empty<OsvQueryResult>();
        }

        if (queries.Count > MaxBatchSize)
        {
            throw new ArgumentException($"At most {MaxBatchSize} queries may be batched.", nameof(queries));
        }

        var payload = new BatchRequest(queries
            .Select(q => new BatchQuery(new BatchPackage(q.Name, q.Ecosystem), q.Version))
            .ToList());

        BatchResponse? response;
        try
        {
            using var message = await _httpClient.PostAsJsonAsync(QueryBatchUrl, payload, cancellationToken);

            if (!message.IsSuccessStatusCode)
            {
                var body = await message.Content.ReadAsStringAsync(cancellationToken);

                // OSV validates the ecosystem and answers an unrecognized one with 400 "invalid
                // ecosystem" rather than an empty result — which is the good failure, and the
                // only reason a distribution it does not cover can be reported as a stated gap
                // instead of as a host with nothing wrong with it. Its own words are carried
                // through to the screen for that reason.
                throw new ExternalServiceException(
                    $"OSV answered {(int)message.StatusCode}: {Summarize(body)}");
            }

            response = await message.Content.ReadFromJsonAsync<BatchResponse>(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ExternalServiceException($"Could not reach OSV: {ex.Message}", ex);
        }

        if (response?.Results is null)
        {
            throw new ExternalServiceException("OSV returned no results for a batch query.");
        }

        if (response.Results.Count != queries.Count)
        {
            // The contract is positional — OSV answers in the order it was asked. A mismatch
            // would silently attribute one package's vulnerabilities to another, so it is refused
            // rather than zipped as far as it goes.
            throw new ExternalServiceException(
                $"OSV answered {response.Results.Count} results for {queries.Count} queries.");
        }

        return queries
            .Zip(response.Results, (query, result) => new OsvQueryResult(
                query,
                (result.Vulns ?? new List<BatchVuln>())
                    .Select(v => v.Id)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetCveIdsAsync(string osvId, CancellationToken cancellationToken)
    {
        VulnRecord? record;
        try
        {
            record = await _httpClient.GetFromJsonAsync<VulnRecord>(VulnUrl + Uri.EscapeDataString(osvId), cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ExternalServiceException($"Could not read OSV record {osvId}: {ex.Message}", ex);
        }

        if (record is null)
        {
            return Array.Empty<string>();
        }

        // Both fields, plus the identifier itself. Every record checked carried its CVEs in
        // `upstream` and none in `aliases`, but `aliases` is the older and more widely populated
        // of the two and other ecosystems use it.
        var candidates = (record.Upstream ?? Enumerable.Empty<string>())
            .Concat(record.Aliases ?? Enumerable.Empty<string>())
            .Append(record.Id ?? osvId);

        var cves = candidates
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim().ToUpperInvariant())
            .Where(id => id.StartsWith("CVE-", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (cves.Count == 0)
        {
            // Real and uninteresting: a distribution-only advisory with no CVE assigned. Logged
            // at debug volume rather than as a problem, and cached so it is not re-fetched.
            _logger.LogDebug("OSV record {OsvId} names no CVE", osvId);
        }

        return cves;
    }

    /// <summary>Keeps an error body short enough to sit in a table cell and a log line.</summary>
    private static string Summarize(string body) =>
        body.Length <= 200 ? body.Trim() : body[..200].Trim() + "…";

    private record BatchRequest([property: JsonPropertyName("queries")] IReadOnlyList<BatchQuery> Queries);

    private record BatchQuery(
        [property: JsonPropertyName("package")] BatchPackage Package,
        [property: JsonPropertyName("version")] string Version);

    private record BatchPackage(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("ecosystem")] string Ecosystem);

    private record BatchResponse([property: JsonPropertyName("results")] IReadOnlyList<BatchResult>? Results);

    private record BatchResult([property: JsonPropertyName("vulns")] List<BatchVuln>? Vulns);

    private record BatchVuln([property: JsonPropertyName("id")] string? Id);

    private record VulnRecord(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("aliases")] IReadOnlyList<string>? Aliases,
        [property: JsonPropertyName("upstream")] IReadOnlyList<string>? Upstream);
}
