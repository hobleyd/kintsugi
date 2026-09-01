namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// The resolved GitHub configuration — stored settings with defaults filled in — as every consumer
/// should see it.
/// </summary>
/// <param name="AgentPackageRepository">Never null: the stored value or the default.</param>
/// <param name="ScriptApprovalRepository">Never null, same rule.</param>
/// <param name="ApiToken">Null when none is stored, which is a supported state — it only lifts the
/// anonymous rate limit.</param>
/// <param name="ScriptApprovalToken">Null when none is stored, which disables publishing approvals.</param>
public record GitHubSettingsSnapshot(
    string AgentPackageRepository,
    string ScriptApprovalRepository,
    string? ApiToken,
    string? ScriptApprovalToken)
{
    /// <summary>Whether a script approval can be published at all. Reported on the Upgrade Scripts
    /// page rather than left to be discovered as pull requests that were never opened.</summary>
    public bool CanPublishScriptApprovals => !string.IsNullOrWhiteSpace(ScriptApprovalToken);
}

/// <summary>
/// Reads the GitHub configuration for whoever needs it right now.
/// </summary>
/// <remarks>
/// Exists as a provider rather than each client reading <c>IConfiguration</c> in its constructor,
/// which is what they all used to do. That was fine while the values came from the environment and
/// could not change without a restart; now that they are edited on a settings page, a value captured
/// at construction is a value that silently ignores every later edit. Every consumer therefore reads
/// through here **per call**.
///
/// Scoped, like the repositories it reads. Nothing that consumes it is a singleton — every consumer
/// is a MediatR handler, and the background coordinators dispatch inside their own scopes.
/// </remarks>
public interface IGitHubSettingsProvider
{
    Task<GitHubSettingsSnapshot> GetAsync(CancellationToken cancellationToken);
}
