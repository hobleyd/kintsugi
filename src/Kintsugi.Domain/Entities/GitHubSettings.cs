using Kintsugi.Domain.Common;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// Singleton configuration for everything this system does against GitHub: which repositories it
/// reads agent builds and script approvals from, and the credentials it uses for each.
/// </summary>
/// <remarks>
/// These lived in the environment (<c>GITHUB_API_TOKEN</c>, <c>AGENT_PACKAGE_GITHUB_REPO</c>,
/// <c>SCRIPT_APPROVAL_GITHUB_REPO</c>, <c>SCRIPT_APPROVAL_GITHUB_TOKEN</c>) until they moved here.
/// The environment is now read exactly once, to seed this row on a deployment that has none — see
/// <c>Program.cs</c>'s <c>SeedGitHubSettingsFromEnvironmentAsync</c> — and is ignored entirely
/// afterwards, so there is one source of truth rather than two to reason about.
///
/// The consequence for anything that consumes these: a value can now change while the process is
/// running. Reading it in a constructor — which is what every GitHub client used to do with
/// <c>IConfiguration</c> — would pin whatever was true at startup, so they read through
/// <c>IGitHubSettingsProvider</c> per call instead.
///
/// Tokens are stored as written, matching <see cref="AiAgentSettings.ApiKey"/> and
/// <see cref="AuthenticationSettings.ClientSecret"/>. The database is not a secret store; what
/// protects these is that nothing ever returns them to a browser (see <c>GitHubSettingsDto</c>).
/// </remarks>
public class GitHubSettings : BaseEntity
{
    /// <summary>Read-only token used to lift GitHub's anonymous rate limit — for upgrade-path
    /// research's repository search and for listing agent-build releases. Needs no scopes at all
    /// against public repositories. Deliberately distinct from
    /// <see cref="ScriptApprovalToken"/>: that one can write, and handing it to the research client
    /// would grant write access for a feature the research client is no part of.</summary>
    public string? ApiToken { get; private set; }

    /// <summary>Where the Clients page pulls kintsugi-agent builds from. Null means the default
    /// (see <c>IGitHubSettingsProvider</c>), which is resolved at read time rather than written here
    /// so the default lives in exactly one place.</summary>
    public string? AgentPackageRepository { get; private set; }

    /// <summary>Where human-approved upgrade scripts are published and read back. Null means the
    /// same default. Its default branch is the trust root for script approval, so changing this
    /// changes whose merges can offer executable content to this server.</summary>
    public string? ScriptApprovalRepository { get; private set; }

    /// <summary>Token with <c>contents:write</c> and <c>pull_requests:write</c> on
    /// <see cref="ScriptApprovalRepository"/>. Null disables publishing: signing still approves a
    /// script locally and its agents still run it, but no pull request is raised and no other server
    /// can pick the approval up — which the Upgrade Scripts page says out loud.</summary>
    public string? ScriptApprovalToken { get; private set; }

    private GitHubSettings()
    {
    }

    public static GitHubSettings Create(
        string? apiToken, string? agentPackageRepository, string? scriptApprovalRepository, string? scriptApprovalToken)
    {
        var settings = new GitHubSettings();
        settings.Apply(apiToken, agentPackageRepository, scriptApprovalRepository, scriptApprovalToken);
        return settings;
    }

    /// <summary>
    /// Applies an edit from the settings page. A blank token means "keep the one already stored"
    /// rather than "clear it", because the UI never round-trips a real token back to the browser and
    /// so cannot send it back unchanged — the same rule <see cref="AiAgentSettings"/> follows for its
    /// API key. Clearing a token is therefore an explicit act; see <see cref="ClearApiToken"/> and
    /// <see cref="ClearScriptApprovalToken"/>.
    /// </summary>
    public void Update(
        string? apiToken, string? agentPackageRepository, string? scriptApprovalRepository, string? scriptApprovalToken)
    {
        Apply(apiToken, agentPackageRepository, scriptApprovalRepository, scriptApprovalToken);
        MarkUpdated();
    }

    public void ClearApiToken()
    {
        ApiToken = null;
        MarkUpdated();
    }

    public void ClearScriptApprovalToken()
    {
        ScriptApprovalToken = null;
        MarkUpdated();
    }

    private void Apply(
        string? apiToken, string? agentPackageRepository, string? scriptApprovalRepository, string? scriptApprovalToken)
    {
        ApiToken = string.IsNullOrWhiteSpace(apiToken) ? ApiToken : apiToken.Trim();
        ScriptApprovalToken = string.IsNullOrWhiteSpace(scriptApprovalToken) ? ScriptApprovalToken : scriptApprovalToken.Trim();

        // A repository, unlike a token, *is* round-tripped to the page, so blank here genuinely means
        // "unset it and fall back to the default" rather than "keep". Trailing slashes trimmed
        // because "owner/repo/" pasted from a browser address bar would otherwise build every API
        // URL with a double slash and 404 on all of them.
        AgentPackageRepository = Normalize(agentPackageRepository);
        ScriptApprovalRepository = Normalize(scriptApprovalRepository);
    }

    private static string? Normalize(string? repository) =>
        string.IsNullOrWhiteSpace(repository) ? null : repository.Trim().Trim('/');
}
