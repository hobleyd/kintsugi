using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Infrastructure.ScriptApproval;

namespace Kintsugi.Infrastructure.GitHubApp;

/// <summary>
/// What an installation token may do: which permissions it carries and which repositories it can
/// reach. GitHub lets a token be minted narrower than the installation, and that is what keeps the
/// read-side consumers from ever holding write access once an App replaces the two stored tokens.
/// </summary>
/// <param name="Name">Part of the cache key, so two scopes never share a token.</param>
/// <param name="Repositories">Repository <em>names</em> (not owner/name) to restrict the token to,
/// or null for every repository the installation can reach.</param>
public record GitHubTokenScope(
    string Name,
    IReadOnlyDictionary<string, string> Permissions,
    IReadOnlyList<string>? Repositories)
{
    /// <summary>
    /// What <c>GitHubSettingsSnapshot.ApiToken</c> becomes in App mode: <c>contents:read</c> on
    /// every repository the installation reaches. It goes to the AI research client and the
    /// agent-package source client as well as the approval reader, so it must not carry the App's
    /// write permissions — see <c>.claude/rules/script-approval-repo.md</c>.
    /// </summary>
    public static readonly GitHubTokenScope ReadOnly = new(
        "read",
        new Dictionary<string, string> { ["contents"] = "read", ["metadata"] = "read" },
        null);

    /// <summary>What <c>ScriptApprovalToken</c> becomes: write access to the approval repository
    /// and nothing else, even when the installation reaches more.</summary>
    public static GitHubTokenScope ApprovalWrite(string repositoryName) => new(
        $"approval-write:{repositoryName}",
        new Dictionary<string, string>
        {
            ["contents"] = "write",
            ["pull_requests"] = "write",
            ["metadata"] = "read",
        },
        new[] { repositoryName });
}

/// <summary>
/// Mints GitHub App installation tokens, and holds each until shortly before it expires.
/// </summary>
/// <remarks>
/// <para>
/// A singleton with its own cache, for the reason <c>VantaAccessTokenProvider</c> is one: every
/// GitHub consumer reads its token through the scoped <c>GitHubSettingsProvider</c> per call, and
/// minting afresh on each of those would spend a GitHub round trip — and an App's token-creation
/// rate limit — on every page load.
/// </para>
/// <para>
/// The cache key covers the App, the installation, the scope and a hash of the private key, so
/// reconnecting a different App, reinstalling, or changing the approval repository on the settings
/// page invalidates the right entries implicitly rather than needing anything to remember to.
/// </para>
/// </remarks>
public class GitHubAppTokenProvider
{
    /// <summary>Installation tokens last an hour. Renewing five minutes early means a token is
    /// never handed to a consumer about to start a slow, multi-request publish with seconds left.</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, (string Token, DateTimeOffset ExpiresUtc)> _cache = new(StringComparer.Ordinal);

    public GitHubAppTokenProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> GetAsync(
        long appId, string privateKeyPem, long installationId, GitHubTokenScope scope, CancellationToken cancellationToken)
    {
        var key = $"{appId}\n{installationId}\n{scope.Name}\n{KeyFingerprint(privateKeyPem)}";

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow < cached.ExpiresUtc - ExpirySkew)
            {
                return cached.Token;
            }

            var issued = await MintAsync(appId, privateKeyPem, installationId, scope, cancellationToken);
            _cache[key] = (issued.Token, issued.ExpiresAt);
            return issued.Token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<TokenResponse> MintAsync(
        long appId, string privateKeyPem, long installationId, GitHubTokenScope scope, CancellationToken cancellationToken)
    {
        // A plain client, not a typed one: this singleton outlives any typed client's handler
        // lifetime, which is the captive-dependency trap IHttpClientFactory exists to avoid.
        using var client = _httpClientFactory.CreateClient();
        ScriptApprovalGitHubHeaders.ApplyStaticHeaders(client);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"https://api.github.com/app/installations/{installationId}/access_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", GitHubAppJwt.Create(appId, privateKeyPem, DateTimeOffset.UtcNow));
        request.Content = JsonContent.Create(new TokenRequest(scope.Permissions, scope.Repositories));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ScriptApprovalGitHubHeaders.MetadataTimeout);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExternalServiceException("GitHub did not answer the installation-token request in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalServiceException($"GitHub could not be reached to mint an installation token: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new ExternalServiceException(
                    $"GitHub would not mint a '{scope.Name}' installation token "
                    + $"({(int)response.StatusCode} {response.ReasonPhrase}). {Truncate(body)}");
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken);
            if (token is null || string.IsNullOrWhiteSpace(token.Token))
            {
                throw new ExternalServiceException("GitHub returned no installation token.");
            }

            return token;
        }
    }

    /// <summary>Keys the cache on the key without holding a second copy of it.</summary>
    private static string KeyFingerprint(string privateKeyPem) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(privateKeyPem)));

    /// <summary>The usual cause of a refusal — a repository the installation was not granted, or
    /// an approval repository on a different account — is named in the body, which ends up in a
    /// log line.</summary>
    private static string Truncate(string body) =>
        body.Length <= 300 ? body : body[..300] + "…";

    private record TokenRequest(
        [property: JsonPropertyName("permissions")] IReadOnlyDictionary<string, string> Permissions,
        [property: JsonPropertyName("repositories"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Repositories);

    private record TokenResponse(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
}
