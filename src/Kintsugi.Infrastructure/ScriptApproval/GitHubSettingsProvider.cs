using Microsoft.Extensions.Logging;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Infrastructure.GitHubApp;

namespace Kintsugi.Infrastructure.ScriptApproval;

/// <inheritdoc cref="IGitHubSettingsProvider" />
/// <remarks>
/// <para>
/// When a GitHub App is connected and installed, this is where it replaces the two stored tokens —
/// and the only place, which is why none of the five consumers had to change. Each token becomes an
/// installation token minted for exactly what that consumer may do: <see cref="GitHubTokenScope.ReadOnly"/>
/// in place of <c>ApiToken</c>, <see cref="GitHubTokenScope.ApprovalWrite"/> for the approval
/// repository alone in place of <c>ScriptApprovalToken</c>. The separation
/// <c>.claude/rules/script-approval-repo.md</c> insists on therefore survives the switch, even
/// though one App now stands behind both.
/// </para>
/// <para>
/// A token that cannot be minted becomes null rather than an exception, with the reason logged. Null
/// is already a supported state for each — anonymous reads, and "signing approves locally and raises
/// no pull request", which the Upgrade Scripts page says out loud — whereas throwing here would take
/// the settings page down with it, the very page needed to fix the App. The stored tokens are
/// deliberately <em>not</em> fallen back to: an App that is installed but failing should be seen to
/// fail, not quietly papered over by a personal token somebody forgot was still saved.
/// </para>
/// </remarks>
public class GitHubSettingsProvider : IGitHubSettingsProvider
{
    /// <summary>
    /// Where both repository settings point when nothing has been configured. This project's own
    /// public repository, which is already named in CLAUDE.md and is not deployment detail.
    ///
    /// Note what the default means for each of the two: for agent builds it is simply where the
    /// releases are, and reading is anonymous. For script approvals it is also the *trust root* —
    /// approving anything requires write access to whatever it names — so anyone who is not this
    /// project's maintainer wants their own repository there.
    /// </summary>
    public const string DefaultRepository = "hobleyd/kintsugi";

    private readonly IGitHubSettingsRepository _repository;
    private readonly GitHubAppTokenProvider _appTokens;
    private readonly ILogger<GitHubSettingsProvider> _logger;

    public GitHubSettingsProvider(
        IGitHubSettingsRepository repository, GitHubAppTokenProvider appTokens, ILogger<GitHubSettingsProvider> logger)
    {
        _repository = repository;
        _appTokens = appTokens;
        _logger = logger;
    }

    public async Task<GitHubSettingsSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        // No caching of the row. A settings page edit has to take effect on the next request, and
        // these reads sit alongside an HTTP call to GitHub that costs orders of magnitude more than
        // the query. Installation tokens *are* cached — by GitHubAppTokenProvider, keyed so that an
        // edit here invalidates them.
        var settings = await _repository.GetAsync(cancellationToken);

        var agentPackageRepository = Or(settings?.AgentPackageRepository, DefaultRepository);
        var scriptApprovalRepository = Or(settings?.ScriptApprovalRepository, DefaultRepository);

        if (settings is { IsGitHubAppInstalled: true })
        {
            return new GitHubSettingsSnapshot(
                agentPackageRepository,
                scriptApprovalRepository,
                await TryMintAsync(settings, GitHubTokenScope.ReadOnly, cancellationToken),
                await TryMintAsync(settings, GitHubTokenScope.ApprovalWrite(RepositoryName(scriptApprovalRepository)), cancellationToken));
        }

        return new GitHubSettingsSnapshot(
            agentPackageRepository,
            scriptApprovalRepository,
            NullIfBlank(settings?.ApiToken),
            NullIfBlank(settings?.ScriptApprovalToken));
    }

    private async Task<string?> TryMintAsync(GitHubSettings settings, GitHubTokenScope scope, CancellationToken cancellationToken)
    {
        try
        {
            return await _appTokens.GetAsync(
                settings.GitHubAppId!.Value,
                settings.GitHubAppPrivateKey!,
                settings.GitHubAppInstallationId!.Value,
                scope,
                cancellationToken);
        }
        catch (ExternalServiceException ex)
        {
            _logger.LogWarning(
                "GitHub App {Slug} could not mint a {Scope} token; continuing without one. {Reason}",
                settings.GitHubAppSlug, scope.Name, ex.Message);
            return null;
        }
    }

    /// <summary>An installation token is restricted by repository <em>name</em>; the owner is
    /// implied by the installation, and an approval repository on a different account is refused
    /// by GitHub — logged above, and shown on the Settings page against the App's owner.</summary>
    private static string RepositoryName(string ownerAndName)
    {
        var slash = ownerAndName.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? ownerAndName : ownerAndName[(slash + 1)..];
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().Trim('/');

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
