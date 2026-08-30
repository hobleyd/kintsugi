using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// One published build of the kintsugi-agent installer package for a given platform ("macos",
/// "windows" or "linux") — what the Clients page offers for download, and what a running agent
/// compares its own version against to decide whether to self-update (see
/// <c>PublishAgentPackageCommandHandler</c> and each agent's own <c>self_update</c> module). Note
/// this platform namespace is the agent build's, and is deliberately separate from
/// <c>PlatformBucket</c>'s upgrade-path buckets.
/// The newest row for a platform (by <see cref="BaseEntity.CreatedAtUtc"/>) is always "the" current
/// version for that platform — there is no separate publish/unpublish step, and a (platform,
/// version) pair's content is never overwritten once published (see
/// <c>PublishAgentPackageCommandHandler</c>: re-publishing identical content under the same
/// (platform, version) is accepted as a no-op, but different content under it is rejected).
/// </summary>
public class AgentPackage : BaseEntity
{
    public string Platform { get; private set; } = default!;
    public string Version { get; private set; } = default!;
    public string FileName { get; private set; } = default!;
    public long FileSizeBytes { get; private set; }

    /// <summary>Lowercase hex SHA-256 of the package file's bytes, computed while it was saved.</summary>
    public string Sha256 { get; private set; } = default!;

    /// <summary>Base64 ECDSA-SHA256 signature over <see cref="Sha256"/>'s ASCII bytes, from the
    /// server's own artifact-signing key (see <c>IArtifactSigningService</c>) — the agent verifies
    /// this against its pinned copy of that key before it trusts a downloaded package enough to
    /// install it over itself, the same way it already does for upgrade scripts/commands.</summary>
    public string Sha256Signature { get; private set; } = default!;

    public string? ReleaseNotes { get; private set; }

    private AgentPackage()
    {
    }

    public static AgentPackage Create(
        string platform,
        string version,
        string fileName,
        long fileSizeBytes,
        string sha256,
        string sha256Signature,
        string? releaseNotes)
    {
        var entity = new AgentPackage();
        entity.Apply(platform, version, fileName, fileSizeBytes, sha256, sha256Signature, releaseNotes);
        return entity;
    }

    private void Apply(
        string platform,
        string version,
        string fileName,
        long fileSizeBytes,
        string sha256,
        string sha256Signature,
        string? releaseNotes)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            throw new DomainException("Platform is required.");
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new DomainException("Version is required.");
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new DomainException("File name is required.");
        }

        if (fileSizeBytes <= 0)
        {
            throw new DomainException("File size must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(sha256))
        {
            throw new DomainException("A SHA-256 checksum is required.");
        }

        if (string.IsNullOrWhiteSpace(sha256Signature))
        {
            throw new DomainException("A signature over the checksum is required.");
        }

        Platform = platform;
        Version = version;
        FileName = fileName;
        FileSizeBytes = fileSizeBytes;
        Sha256 = sha256;
        Sha256Signature = sha256Signature;
        ReleaseNotes = string.IsNullOrWhiteSpace(releaseNotes) ? null : releaseNotes;
    }
}
