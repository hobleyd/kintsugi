using MediatR;

namespace Kintsugi.Application.GitHub.Commands.CompleteGitHubAppManifest;

/// <summary>
/// Finishes the manifest flow: exchanges the one-time code GitHub redirected back with for the new
/// App's credentials and stores them.
/// </summary>
/// <remarks>
/// Answers with the App's slug, because the very next step — installing it — is a page on GitHub
/// addressed by that slug, and the caller redirects the browser there.
/// </remarks>
public record CompleteGitHubAppManifestCommand(string Code) : IRequest<string>;
