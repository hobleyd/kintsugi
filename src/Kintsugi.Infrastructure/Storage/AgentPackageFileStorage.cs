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
        var directory = Path.Combine(_rootDirectory, platform);
        Directory.CreateDirectory(directory);
        var destinationPath = Path.Combine(directory, fileName);

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
        File.OpenRead(Path.Combine(_rootDirectory, platform, fileName));
}
