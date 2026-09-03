using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.Vanta;

/// <summary>
/// Holds the OAuth access token the Vanta API is called with, and renews it when it expires.
/// </summary>
/// <remarks>
/// <para>
/// A singleton with its own cache, and that is the whole point of it existing separately from
/// <see cref="VantaSyncClient"/>. Vanta allows exactly one active access token per application and
/// <em>revokes the previous one the moment a new one is issued</em>, so two components each fetching
/// their own token would spend the sync invalidating each other. One cache, one
/// <see cref="SemaphoreSlim"/>, one token.
/// </para>
/// <para>
/// The cache is keyed on the credentials it was obtained with, so rotating the client secret on the
/// settings page invalidates it implicitly rather than needing anything to remember to. That matters
/// because the settings are editable while the process runs — the same reason nothing here pins a
/// bearer token to <c>HttpClient.DefaultRequestHeaders</c>; see <see cref="IVantaSettingsProvider"/>.
/// </para>
/// </remarks>
public class VantaAccessTokenProvider
{
    /// <summary>Vanta issues one-hour tokens. Treating one as expired a minute early costs a token
    /// exchange; treating one as valid a second too long costs a 401 in the middle of a sync.</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(1);

    /// <summary>The scopes this integration needs, and no others: read is not requested, because
    /// nothing here ever reads resources back.</summary>
    public const string Scope = "connectors.self:write-resource";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _cacheKey;
    private string? _token;
    private DateTimeOffset _expiresUtc;

    public VantaAccessTokenProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// The current access token, obtaining a fresh one if none is cached, the cached one belongs to
    /// different credentials, or it is about to expire.
    /// </summary>
    /// <param name="forceRefresh">Set after a 401, which is the one case where a token that looks
    /// valid here demonstrably is not — Vanta revokes tokens out from under a caller whenever
    /// anything else asks for one.</param>
    public async Task<string> GetAsync(VantaSettingsSnapshot settings, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            throw new ExternalServiceException("Vanta is not configured with an OAuth client ID and secret.");
        }

        var key = $"{settings.ApiBaseUrl}\n{settings.ClientId}\n{settings.ClientSecret}";

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh
                && _token is not null
                && _cacheKey == key
                && DateTimeOffset.UtcNow < _expiresUtc - ExpirySkew)
            {
                return _token;
            }

            var issued = await RequestTokenAsync(settings, cancellationToken);

            _cacheKey = key;
            _token = issued.AccessToken;
            _expiresUtc = DateTimeOffset.UtcNow.AddSeconds(issued.ExpiresIn);

            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<TokenResponse> RequestTokenAsync(VantaSettingsSnapshot settings, CancellationToken cancellationToken)
    {
        // A plain client rather than a typed one: this is the only call in the system that must not
        // carry an Authorization header, and giving it its own client keeps that impossible to get
        // wrong by accident.
        using var client = _httpClientFactory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"{settings.ApiBaseUrl}/oauth/token",
            new
            {
                client_id = settings.ClientId,
                client_secret = settings.ClientSecret,
                scope = Scope,
                grant_type = "client_credentials",
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ExternalServiceException(
                $"Vanta rejected the credentials ({(int)response.StatusCode} {response.ReasonPhrase}). {Truncate(body)}");
        }

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new ExternalServiceException("Vanta returned no access token.");
        }

        return token;
    }

    /// <summary>A rejected credential's body can be long; the message it ends up in is shown on a
    /// settings page.</summary>
    private static string Truncate(string body) =>
        body.Length <= 300 ? body : body[..300] + "…";

    private record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
