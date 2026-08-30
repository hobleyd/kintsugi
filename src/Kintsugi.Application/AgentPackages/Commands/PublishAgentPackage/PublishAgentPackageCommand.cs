using MediatR;

namespace Kintsugi.Application.AgentPackages.Commands.PublishAgentPackage;

/// <summary>
/// Publishes a new build for a platform. Built by <c>AgentPackagesController.Publish</c> from a
/// multipart upload — meant to be called from a release script (see
/// clients/macos-agent/packaging/publish-release.sh), not from the browser UI.
/// </summary>
public record PublishAgentPackageCommand(
    string Platform,
    string Version,
    string? ReleaseNotes,
    string FileName,
    Stream Content) : IRequest<AgentPackageDto>;
