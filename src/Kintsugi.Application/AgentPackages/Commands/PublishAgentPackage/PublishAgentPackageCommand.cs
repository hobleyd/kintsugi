using MediatR;

namespace Kintsugi.Application.AgentPackages.Commands.PublishAgentPackage;

/// <summary>
/// Publishes a new build for a platform. Built by <c>AgentPackagesController.Publish</c> from a
/// multipart upload — meant to be called from a release script (see
/// clients/macos-agent/packaging/publish-release.sh), not from the browser UI.
/// </summary>
/// <param name="UpstreamSha256">Lowercase hex SHA-256 of the pristine upstream archive, when this
/// publish is an import from GitHub rather than a release script's own upload. Recorded because
/// <see cref="Content"/> has already had its <c>api_base_url</c> rewritten by then, so the checksum
/// taken over it here describes this server's bytes and not the ones GitHub serves — and the
/// Windows bootstrap script pins the latter. Null from a release script, which has no upstream.</param>
/// <param name="UpstreamDownloadUrl">Where those bytes were fetched from. Travels with the hash;
/// see <c>AgentPackage.UpstreamDownloadUrl</c>.</param>
public record PublishAgentPackageCommand(
    string Platform,
    string Version,
    string? ReleaseNotes,
    string FileName,
    Stream Content,
    string? UpstreamSha256 = null,
    string? UpstreamDownloadUrl = null) : IRequest<AgentPackageDto>;
