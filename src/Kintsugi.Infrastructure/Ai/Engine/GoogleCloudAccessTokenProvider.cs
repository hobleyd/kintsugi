using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Kintsugi.Application.Common.Exceptions;

namespace Kintsugi.Infrastructure.Ai.Engine;

/// <summary>
/// A Google Cloud access token for the identity this server runs as, from the metadata server.
/// </summary>
/// <remarks>
/// <para>
/// On GKE with Workload Identity the metadata server answers as the pod's Google service account,
/// so Vertex AI is reached with <b>no key stored anywhere</b> — the platform mints a token per hour.
/// The same call works on a GCE VM or Cloud Run. Off Google Cloud it fails, and the failure says
/// so rather than pretending a credential is wrong.
/// </para>
/// <para>
/// A singleton with its own cache, for the reason <c>VantaAccessTokenProvider</c> is one: every
/// research call would otherwise spend a metadata round trip. The metadata host is reached with a
/// client of its own, never the one <see cref="SafeWebFetcher"/> uses — that one refuses this
/// address on purpose.
/// </para>
/// </remarks>
public class GoogleCloudAccessTokenProvider
{
    public const string TokenUrl = "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token";

    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(2);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresUtc;

    public GoogleCloudAccessTokenProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresUtc - ExpirySkew)
            {
                return _token;
            }

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using var request = new HttpRequestMessage(HttpMethod.Get, TokenUrl);
            request.Headers.Add("Metadata-Flavor", "Google");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new ExternalServiceException(
                    "Could not reach the Google Cloud metadata server for a token. Google Cloud authentication only works "
                    + "where this server runs on Google Cloud (GKE with Workload Identity, a VM, Cloud Run).", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new ExternalServiceException(
                        $"The Google Cloud metadata server refused a token (HTTP {(int)response.StatusCode}). On GKE, check the "
                        + "Kubernetes service account is bound to a Google service account with Workload Identity.");
                }

                var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken);
                if (string.IsNullOrWhiteSpace(token?.AccessToken))
                {
                    throw new ExternalServiceException("The Google Cloud metadata server returned no access token.");
                }

                _token = token.AccessToken;
                _expiresUtc = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
                return _token;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
