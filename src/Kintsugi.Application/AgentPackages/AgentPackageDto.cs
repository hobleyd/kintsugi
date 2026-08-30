using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.AgentPackages;

public record AgentPackageDto(
    string Platform,
    string Version,
    string FileName,
    long FileSizeBytes,
    string Sha256,
    string Sha256Signature,
    string? ReleaseNotes,
    DateTimeOffset PublishedUtc)
{
    public static AgentPackageDto FromEntity(AgentPackage package) =>
        new(
            package.Platform,
            package.Version,
            package.FileName,
            package.FileSizeBytes,
            package.Sha256,
            package.Sha256Signature,
            package.ReleaseNotes,
            package.CreatedAtUtc);
}
