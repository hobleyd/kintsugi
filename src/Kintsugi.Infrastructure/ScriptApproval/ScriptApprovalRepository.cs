using System.Net.Http.Headers;

namespace Kintsugi.Infrastructure.ScriptApproval;

/// <summary>
/// The headers GitHub's API requires, applied identically by every client that talks to it.
/// </summary>
/// <remarks>
/// The repository names and tokens that used to live here as configuration keys now come from
/// <c>IGitHubSettingsProvider</c>, read per call — see <c>GitHubSettings</c> for why a value that can
/// be edited on a settings page cannot be captured in a constructor.
///
/// The token is attached per request rather than to <c>DefaultRequestHeaders</c>, for the same
/// reason: a typed <c>HttpClient</c> instance outlives an individual call, and pinning an
/// <c>Authorization</c> header to it would carry whatever token was current when the first call on
/// that instance happened to read one.
/// </remarks>
public static class ScriptApprovalGitHubHeaders
{
    /// <summary>Status checks run on every page load, so they get a short leash — a slow or
    /// unreachable GitHub must not hold a page open for the HttpClient's default hundred seconds.</summary>
    public static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The headers that never change, set once per client instance. GitHub rejects an API
    /// request with no User-Agent outright, with a 403 that says nothing about the real cause.</summary>
    public static void ApplyStaticHeaders(HttpClient httpClient)
    {
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Kintsugi-Server");
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>A request carrying the current token, if there is one.</summary>
    public static HttpRequestMessage Request(HttpMethod method, string url, string? token)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        return request;
    }
}
