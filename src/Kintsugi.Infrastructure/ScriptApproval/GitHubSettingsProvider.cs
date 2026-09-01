using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.ScriptApproval;

/// <inheritdoc cref="IGitHubSettingsProvider" />
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

    public GitHubSettingsProvider(IGitHubSettingsRepository repository)
    {
        _repository = repository;
    }

    public async Task<GitHubSettingsSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        // No caching. A settings page edit has to take effect on the next request, and these reads
        // sit alongside an HTTP call to GitHub that costs orders of magnitude more than the query.
        var settings = await _repository.GetAsync(cancellationToken);

        return new GitHubSettingsSnapshot(
            Or(settings?.AgentPackageRepository, DefaultRepository),
            Or(settings?.ScriptApprovalRepository, DefaultRepository),
            NullIfBlank(settings?.ApiToken),
            NullIfBlank(settings?.ScriptApprovalToken));
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().Trim('/');

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
