using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.Storage;

public class AgentPackageArchiveRewriter : IAgentPackageArchiveRewriter
{
    private const string ConfigEntryName = "config.toml";
    private const string EnrollmentTokenKey = "enrollment_token";

    /// <summary>
    /// Decompresses/reads <paramref name="sourceGzipTar"/> fully, then re-encodes it with
    /// <paramref name="enrollmentToken"/> substituted into the <c>config.toml</c> entry. Reading
    /// the whole archive into memory is fine here — a kintsugi-agent install bundle is a few MB,
    /// and this only ever runs once per download request, not in a hot loop.
    /// </summary>
    public async Task<Stream> WithEnrollmentToken(Stream sourceGzipTar, string? enrollmentToken, CancellationToken cancellationToken)
    {
        using var decompressed = new MemoryStream();
        await using (var gzipIn = new GZipStream(sourceGzipTar, CompressionMode.Decompress, leaveOpen: true))
        {
            await gzipIn.CopyToAsync(decompressed, cancellationToken);
        }
        decompressed.Position = 0;

        var output = new MemoryStream();
        await using (var gzipOut = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            await using var tarWriter = new TarWriter(gzipOut, leaveOpen: true);
            using var tarReader = new TarReader(decompressed, leaveOpen: true);

            TarEntry? entry;
            while ((entry = await tarReader.GetNextEntryAsync(cancellationToken: cancellationToken)) is not null)
            {
                if (entry.DataStream is not null && string.Equals(entry.Name, ConfigEntryName, StringComparison.Ordinal))
                {
                    entry.DataStream = await RewriteConfigEntryAsync(entry.DataStream, enrollmentToken, cancellationToken);
                }

                await tarWriter.WriteEntryAsync(entry, cancellationToken);
            }
        }

        output.Position = 0;
        return output;
    }

    private static async Task<MemoryStream> RewriteConfigEntryAsync(Stream original, string? enrollmentToken, CancellationToken cancellationToken)
    {
        string content;
        using (var reader = new StreamReader(original, leaveOpen: true))
        {
            content = await reader.ReadToEndAsync(cancellationToken);
        }

        // Same approach packaging/install.sh's own token handling uses: drop any existing
        // enrollment_token line(s) and append the current value as the last line, rather than
        // trying to edit one in place.
        var withoutExistingToken = string.Join(
            '\n',
            content.Split('\n').Where(line => !line.TrimStart().StartsWith(EnrollmentTokenKey, StringComparison.Ordinal)));

        var rewritten = withoutExistingToken.TrimEnd('\n') + $"\n{EnrollmentTokenKey} = \"{EscapeTomlString(enrollmentToken ?? string.Empty)}\"\n";

        return new MemoryStream(Encoding.UTF8.GetBytes(rewritten));
    }

    // TOML basic-string escaping only needs backslashes and double quotes handled for a value this
    // constrained (a shared secret with no newlines) — same as packaging/install.sh's own handling
    // of this same value.
    private static string EscapeTomlString(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
