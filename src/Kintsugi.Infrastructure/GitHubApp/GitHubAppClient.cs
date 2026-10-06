using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Infrastructure.ScriptApproval;

namespace Kintsugi.Infrastructure.GitHubApp;

/// <inheritdoc cref="IGitHubAppClient" />
public class GitHubAppClient : IGitHubAppClient
{
    private readonly HttpClient _httpClient;

    public GitHubAppClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        ScriptApprovalGitHubHeaders.ApplyStaticHeaders(_httpClient);
    }

    public async Task<GitHubAppCredentials> ConvertManifestAsync(string code, CancellationToken cancellationToken)
    {
        // Unauthenticated by design: the code itself is the credential, issued to the browser that
        // just created the App and good for one conversion within the hour.
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"https://api.github.com/app-manifests/{Uri.EscapeDataString(code)}/conversions");
        using var response = await SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new ExternalServiceException(
                $"GitHub would not convert the App manifest ({(int)response.StatusCode} {response.ReasonPhrase}). "
                + "The code is single-use and expires after an hour — start the GitHub App setup again.");
        }

        var conversion = await response.Content.ReadFromJsonAsync<ManifestConversion>(cancellationToken: cancellationToken);
        if (conversion is null || conversion.Id <= 0 || string.IsNullOrWhiteSpace(conversion.Pem)
            || string.IsNullOrWhiteSpace(conversion.Slug) || string.IsNullOrWhiteSpace(conversion.Owner?.Login))
        {
            throw new ExternalServiceException("GitHub converted the App manifest but returned no usable credentials.");
        }

        return new GitHubAppCredentials(conversion.Id, conversion.Slug, conversion.Owner.Login, conversion.Pem);
    }

    public async Task<string> GetInstallationAccountAsync(
        long appId, string privateKeyPem, long installationId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/app/installations/{installationId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", GitHubAppJwt.Create(appId, privateKeyPem, DateTimeOffset.UtcNow));

        using var response = await SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // What GitHub answers for an installation of a different App, as well as for one that
            // does not exist — either way, not ours to mint for.
            throw new ExternalServiceException(
                $"Installation {installationId} does not belong to this server's GitHub App.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ExternalServiceException(
                $"GitHub would not describe installation {installationId} ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }

        var installation = await response.Content.ReadFromJsonAsync<Installation>(cancellationToken: cancellationToken);
        if (installation?.Account?.Login is not { Length: > 0 } login)
        {
            throw new ExternalServiceException($"GitHub described installation {installationId} without an account.");
        }

        return login;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ScriptApprovalGitHubHeaders.MetadataTimeout);
        try
        {
            return await _httpClient.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExternalServiceException("GitHub did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalServiceException($"GitHub could not be reached: {ex.Message}");
        }
    }

    private record Account([property: JsonPropertyName("login")] string? Login);

    private record ManifestConversion(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("slug")] string? Slug,
        [property: JsonPropertyName("pem")] string? Pem,
        [property: JsonPropertyName("owner")] Account? Owner);

    private record Installation([property: JsonPropertyName("account")] Account? Account);
}
