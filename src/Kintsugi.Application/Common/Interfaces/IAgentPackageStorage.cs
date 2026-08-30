namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Where a published agent package's actual bytes live — deliberately not in the database (see
/// <c>AgentPackage</c>, which only ever stores metadata about a file this saves). Backed by a
/// directory on a persistent volume in production (see <c>AgentPackageFileStorage</c>), so
/// published builds survive an image rebuild/redeploy the same way the agent fleet's CA does.
/// </summary>
public interface IAgentPackageStorage
{
    /// <summary>Writes <paramref name="content"/> to storage under <paramref name="platform"/> /
    /// <paramref name="fileName"/>, hashing it as it's written rather than requiring a second pass
    /// over the file. Returns the resulting file size and its lowercase hex SHA-256.</summary>
    Task<(long FileSizeBytes, string Sha256Hex)> SaveAsync(
        string platform, string fileName, Stream content, CancellationToken cancellationToken);

    /// <summary>Opens a previously saved file for reading — the caller is responsible for
    /// disposing the returned stream.</summary>
    Stream OpenRead(string platform, string fileName);
}
