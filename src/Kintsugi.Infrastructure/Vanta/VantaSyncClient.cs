using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Vanta;

namespace Kintsugi.Infrastructure.Vanta;

/// <summary>
/// Sends resource collections to Vanta's "Build integrations" API — <c>PUT /v1/resources/{type}</c>,
/// per https://developer.vanta.com/reference/build-integrations.json.
/// </summary>
public class VantaSyncClient : IVantaSyncClient
{
    /// <summary>The full fleet goes in one request and there is no chunked form of it (see
    /// <see cref="IVantaSyncClient"/>), so this needs to be generous — but not the HttpClient's
    /// default hundred seconds multiplied by however long a hung connection sits there, because the
    /// background service holds the single-run lock for the whole time.</summary>
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(5);

    /// <summary>camelCase to match the schema's property names, and nulls dropped so an absent
    /// optional field is genuinely absent rather than an explicit <c>null</c> — which matters for
    /// the fields this integration deliberately never populates (<c>cveId</c>, <c>cvss3Score</c>,
    /// <c>cvss3Vector</c>).</summary>
    /// <remarks>
    /// Public so a test can assert the emitted property names against the spec's required lists.
    /// Nothing else checks them: a renamed record property or a stray <c>[JsonIgnore]</c> would pass
    /// every other test and surface only as Vanta's own rejection message in the settings screen's
    /// status line.
    /// </remarks>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly IVantaSettingsProvider _settingsProvider;
    private readonly VantaAccessTokenProvider _tokenProvider;

    public VantaSyncClient(
        HttpClient httpClient,
        IVantaSettingsProvider settingsProvider,
        VantaAccessTokenProvider tokenProvider)
    {
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
        _tokenProvider = tokenProvider;
    }

    public Task SyncVulnerableComponentsAsync(
        IReadOnlyList<VantaVulnerableComponent> components, CancellationToken cancellationToken) =>
        SyncAsync(
            "vulnerable_component",
            settings => settings.VulnerableComponentResourceId,
            "VulnerableComponent",
            components,
            cancellationToken);

    public Task SyncPackageVulnerabilitiesAsync(
        IReadOnlyList<VantaPackageVulnerability> packages, CancellationToken cancellationToken) =>
        SyncAsync(
            "package_vulnerability_connectors",
            settings => settings.PackageVulnerabilityResourceId,
            "PackageVulnerabilityConnectors",
            packages,
            cancellationToken);

    private async Task SyncAsync<T>(
        string endpoint,
        Func<VantaSettingsSnapshot, string?> resourceIdSelector,
        string resourceDescription,
        IReadOnlyList<T> resources,
        CancellationToken cancellationToken)
    {
        // Read per call. The settings page can change the credentials, the host and the resource IDs
        // while this process is running; see IVantaSettingsProvider.
        var settings = await _settingsProvider.GetAsync(cancellationToken);

        var resourceId = resourceIdSelector(settings);
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            throw new ExternalServiceException(
                $"No Vanta resource ID is configured for {resourceDescription}. Copy it from the developer console's Resources tab.");
        }

        var payload = new { resourceId, resources };
        var url = $"{settings.ApiBaseUrl}/v1/resources/{endpoint}";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SyncTimeout);

        try
        {
            var response = await SendAsync(url, payload, settings, forceFreshToken: false, timeout.Token);

            // One retry, and only on a 401. Vanta revokes the previous access token whenever a new
            // one is issued, so a token that was valid when this sync started can be dead by the
            // time the second request goes out — another sync run, or another process sharing these
            // credentials, only has to have asked for one. Anything other than a 401 is a real
            // failure and is reported rather than retried.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                response = await SendAsync(url, payload, settings, forceFreshToken: true, timeout.Token);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(timeout.Token);
                    throw new ExternalServiceException(
                        $"Vanta rejected the {resourceDescription} sync ({(int)response.StatusCode} {response.ReasonPhrase}). {Truncate(body)}");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller didn't cancel, so this is SyncTimeout firing. Reported as a timeout rather
            // than propagating a cancellation the caller would read as "the request went away".
            throw new ExternalServiceException(
                $"Syncing {resourceDescription} to Vanta took longer than {SyncTimeout.TotalMinutes:0} minutes.");
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalServiceException($"Could not reach Vanta at {settings.ApiBaseUrl}: {ex.Message}", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        string url, object payload, VantaSettingsSnapshot settings, bool forceFreshToken, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetAsync(settings, forceFreshToken, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };

        // On the request, never on HttpClient.DefaultRequestHeaders: a typed HttpClient instance
        // outlives any one call, so a header pinned to it would carry whichever token was current
        // the first time — the exact bug the GitHub clients were rewritten to avoid (see CLAUDE.md).
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private static string Truncate(string body) => body.Length <= 300 ? body : body[..300] + "…";
}
