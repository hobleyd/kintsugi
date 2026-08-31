using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;

namespace Kintsugi.Infrastructure.ScriptApproval;

/// <summary>
/// Which repository script approvals are published to and read back from, and the credential for
/// writing to it. Shared by <see cref="GitHubScriptApprovalPublisher"/> and
/// <see cref="GitHubScriptApprovalSourceClient"/> so the two halves of the round trip can never end
/// up pointed at different repositories.
/// </summary>
public static class ScriptApprovalRepository
{
    /// <summary>Overridable so a fork, or a private mirror, can hold a fleet's approvals instead —
    /// the same shape as <c>GitHubAgentPackageSourceClient.RepositoryConfigurationKey</c>. The default
    /// is this project's own public repository, which is already named in CLAUDE.md and is not
    /// deployment detail; an operator who is not this project's maintainer will want to point this at
    /// their own repository, since approving anything requires write access to whatever it names.</summary>
    public const string RepositoryConfigurationKey = "SCRIPT_APPROVAL_GITHUB_REPO";

    /// <summary>
    /// A token with <c>contents:write</c> and <c>pull_requests:write</c> on the configured repository.
    ///
    /// Deliberately *not* <c>GITHUB_API_TOKEN</c>, which exists only to lift the anonymous rate limit
    /// and is handed to the AI research client and the agent-package source client as well. Reusing it
    /// would silently give both of those write access to the repository, for a feature neither of them
    /// is part of. Unset means publication is disabled — reads still work, since the corpus is public.
    /// </summary>
    public const string TokenConfigurationKey = "SCRIPT_APPROVAL_GITHUB_TOKEN";

    private const string DefaultRepository = "hobleyd/kintsugi";

    public static string Resolve(IConfiguration configuration)
    {
        var configured = configuration[RepositoryConfigurationKey];
        return string.IsNullOrWhiteSpace(configured) ? DefaultRepository : configured.Trim().Trim('/');
    }

    public static string? ResolveToken(IConfiguration configuration)
    {
        var token = configuration[TokenConfigurationKey];
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }
}

/// <summary>
/// The headers GitHub's API requires, applied identically by both clients.
/// </summary>
public static class ScriptApprovalGitHubHeaders
{
    /// <summary>The status check runs on every Upgrade Scripts page load, so it gets the same short
    /// leash <c>GitHubAgentPackageSourceClient</c> gives its release listing — a slow or unreachable
    /// GitHub must not hold the page open for the HttpClient's default hundred seconds.</summary>
    public static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(10);

    public static void Apply(HttpClient httpClient, string? token)
    {
        // GitHub rejects an API request with no User-Agent outright, with a 403 that says nothing
        // about the real cause.
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Kintsugi-Server");
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        if (!string.IsNullOrWhiteSpace(token))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }
    }
}
