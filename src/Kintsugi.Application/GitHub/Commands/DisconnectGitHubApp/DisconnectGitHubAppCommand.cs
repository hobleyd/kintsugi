using MediatR;

namespace Kintsugi.Application.GitHub.Commands.DisconnectGitHubApp;

/// <summary>
/// Forgets the connected GitHub App, after which the stored tokens (if any) apply again. The App
/// itself is left on GitHub — deleting it is its owner's act, and the Settings page says where.
/// </summary>
public record DisconnectGitHubAppCommand : IRequest<GitHubSettingsDto>;
