using MediatR;

namespace Kintsugi.Application.GitHub.Commands.RecordGitHubAppInstallation;

/// <summary>
/// Stores the installation the connected App's tokens are minted for, after GitHub's setup
/// redirect hands it back.
/// </summary>
/// <remarks>
/// The id arrives on a query string, so it is never taken on trust: the handler asks GitHub, as the
/// App, whether the installation is the App's own before recording it.
/// </remarks>
public record RecordGitHubAppInstallationCommand(long InstallationId) : IRequest<GitHubSettingsDto>;
