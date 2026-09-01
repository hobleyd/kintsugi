namespace Kintsugi.Application.GitHub;

/// <summary>
/// The GitHub settings as the settings page sees them.
/// </summary>
/// <remarks>
/// Neither token is ever returned — <see cref="HasApiToken"/> and
/// <see cref="HasScriptApprovalToken"/> report only whether one is stored, the same contract
/// <c>AiAgentSettingsDto</c> has for its API key. That is also why a blank token on save means "keep
/// the stored one": the page cannot send back a value it was never given.
/// </remarks>
/// <param name="AgentPackageRepository">Resolved — the stored value, or the default when none is
/// stored. The page shows the effective value rather than an empty box, since "empty" and "the
/// default" mean the same thing here and only one of them is informative.</param>
/// <param name="IsAgentPackageRepositoryDefault">Whether the value above is the default rather than
/// something an administrator chose.</param>
public record GitHubSettingsDto(
    string AgentPackageRepository,
    bool IsAgentPackageRepositoryDefault,
    string ScriptApprovalRepository,
    bool IsScriptApprovalRepositoryDefault,
    bool HasApiToken,
    bool HasScriptApprovalToken);
