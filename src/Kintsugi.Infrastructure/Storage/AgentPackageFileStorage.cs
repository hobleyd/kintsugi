using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.Storage;

/// <summary>
/// Stores published agent packages as plain files under a directory on a persistent volume (see
/// the api service's <c>agent-packages</c> volume in docker-compose.yml) — the same
/// pattern <see cref="Security.CaService"/> and <see cref="Security.ArtifactSigningService"/> use
/// for their own <c>/data/...</c> state, so a published build survives an image
/// rebuild/redeploy rather than living only in the container's own filesystem.
/// </summary>
public class AgentPackageFileStorage : IAgentPackageStorage
{
    private readonly string _rootDirectory;

    public AgentPackageFileStorage(IConfiguration configuration)
    {
        _rootDirectory = configuration["AgentPackages:Directory"] ?? "/data/agent-packages";
    }

    public async Task<(long FileSizeBytes, string Sha256Hex)> SaveAsync(
        string platform, string fileName, Stream content, CancellationToken cancellationToken)
    {
        // Both names checked before anything is created, so a refused request leaves no trace —
        // not even an empty platform directory named by the caller.
        var platformName = RequireBareName(platform, nameof(platform));
        var bareFileName = RequireBareName(fileName, nameof(fileName));

        var directory = Path.Combine(_rootDirectory, platformName);
        Directory.CreateDirectory(directory);
        var destinationPath = Path.Combine(directory, bareFileName);

        using var sha256 = SHA256.Create();
        await using (var destination = File.Create(destinationPath))
        await using (var hashingStream = new CryptoStream(destination, sha256, CryptoStreamMode.Write))
        {
            await content.CopyToAsync(hashingStream, cancellationToken);
        }

        var fileSizeBytes = new FileInfo(destinationPath).Length;
        var sha256Hex = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
        return (fileSizeBytes, sha256Hex);
    }

    public Stream OpenRead(string platform, string fileName) =>
        File.OpenRead(Path.Combine(_rootDirectory, RequireBareName(platform, nameof(platform)), RequireBareName(fileName, nameof(fileName))));

    /// <summary>
    /// Refuses anything that is not a single path segment: separators, a rooted path, and the two
    /// dot entries. <c>Path.Combine</c> happily joins "../x" (and discards everything before a
    /// rooted second argument), so a name taken from a request would otherwise choose its own
    /// directory. PublishAgentPackageCommandValidator rejects such names first, but this class is
    /// the one that touches the disk, and it is reached from more than one command — so it checks
    /// for itself rather than trusting that every caller was validated.
    /// </summary>
    public static string RequireBareName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || Path.IsPathRooted(value)
            || value.IndexOfAny(new[] { '/', '\\' }) >= 0
            || !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{value}' is not a bare file name.", parameterName);
        }

        return value;
    }
}
