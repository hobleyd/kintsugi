using MediatR;

namespace Kintsugi.Application.GitHub.Commands.UpdateGitHubSettings;

/// <summary>
/// Saves the GitHub settings page.
/// </summary>
/// <param name="ApiToken">Blank keeps whatever is stored — the page never received the real value,
/// so it cannot send it back unchanged. Use <paramref name="ClearApiToken"/> to remove one.</param>
/// <param name="ClearApiToken">Explicitly removes the stored token, which is the only way to go from
/// having one to having none.</param>
public record UpdateGitHubSettingsCommand(
    string? AgentPackageRepository,
    string? ScriptApprovalRepository,
    string? ApiToken,
    bool ClearApiToken,
    string? ScriptApprovalToken,
    bool ClearScriptApprovalToken) : IRequest<GitHubSettingsDto>;
