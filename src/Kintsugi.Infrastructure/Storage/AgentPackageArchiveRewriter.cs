using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.Storage;

public class AgentPackageArchiveRewriter : IAgentPackageArchiveRewriter
{
    private const string ConfigEntryName = "config.toml";
    private const string EnrollmentTokenKey = "enrollment_token";
    private const string ApiBaseUrlKey = "api_base_url";

    /// <inheritdoc />
    public Task<Stream> WithEnrollmentToken(Stream sourceGzipTar, string? enrollmentToken, CancellationToken cancellationToken) =>
        // Drop any existing enrollment_token line(s) and append the current value as the last
        // line, rather than trying to edit one in place — the same approach packaging/install.sh's
        // own token handling uses. Unlike api_base_url below, this value has no explanatory
        // comment worth keeping it next to: the bundled config.toml ships it blank.
        RewriteConfigAsync(
            sourceGzipTar,
            content => DropLinesFor(content, EnrollmentTokenKey).TrimEnd('\n')
                + $"\n{TomlAssignment(EnrollmentTokenKey, enrollmentToken ?? string.Empty)}\n",
            cancellationToken);

    /// <inheritdoc />
    public Task<Stream> WithApiBaseUrl(Stream sourceGzipTar, string apiBaseUrl, CancellationToken cancellationToken) =>
        // In place, not drop-and-append: the bundled config.toml sits this line directly under the
        // comment block explaining what it is and what to restart after changing it, and that
        // comment is the first thing whoever unpacks the tarball reads. Moving the value to the
        // bottom of the file would leave the comment pointing at nothing.
        RewriteConfigAsync(
            sourceGzipTar,
            content => ReplaceOrAppendLine(content, ApiBaseUrlKey, TomlAssignment(ApiBaseUrlKey, apiBaseUrl)),
            cancellationToken);

    /// <summary>
    /// Decompresses/reads <paramref name="sourceGzipTar"/> fully, then re-encodes it with
    /// <paramref name="rewriteConfig"/> applied to the <c>config.toml</c> entry's text. Reading
    /// the whole archive into memory is fine here — a kintsugi-agent install bundle is a few MB,
    /// and this only ever runs once per download or import, not in a hot loop.
    /// </summary>
    private static async Task<Stream> RewriteConfigAsync(
        Stream sourceGzipTar,
        Func<string, string> rewriteConfig,
        CancellationToken cancellationToken)
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
                    entry.DataStream = await RewriteConfigEntryAsync(entry.DataStream, rewriteConfig, cancellationToken);
                }

                await tarWriter.WriteEntryAsync(entry, cancellationToken);
            }
        }

        output.Position = 0;
        return output;
    }

    private static async Task<MemoryStream> RewriteConfigEntryAsync(
        Stream original,
        Func<string, string> rewriteConfig,
        CancellationToken cancellationToken)
    {
        string content;
        using (var reader = new StreamReader(original, leaveOpen: true))
        {
            content = await reader.ReadToEndAsync(cancellationToken);
        }

        return new MemoryStream(Encoding.UTF8.GetBytes(rewriteConfig(content)));
    }

    private static string DropLinesFor(string content, string key) =>
        string.Join('\n', content.Split('\n').Where(line => !IsAssignmentTo(line, key)));

    private static string ReplaceOrAppendLine(string content, string key, string replacement)
    {
        var rewrittenLines = new List<string>();
        var replaced = false;

        foreach (var line in content.Split('\n'))
        {
            if (!IsAssignmentTo(line, key))
            {
                rewrittenLines.Add(line);
                continue;
            }

            // Only the first assignment survives. A duplicate left further down is a coin flip —
            // some TOML parsers take the last one, others reject the file outright — so keeping
            // one is never right.
            if (!replaced)
            {
                rewrittenLines.Add(replacement);
                replaced = true;
            }
        }

        var rewritten = string.Join('\n', rewrittenLines);
        return replaced ? rewritten : rewritten.TrimEnd('\n') + $"\n{replacement}\n";
    }

    /// <summary>Matches a TOML assignment to <paramref name="key"/> and nothing else — notably not
    /// the commented-out mentions of both keys that the bundled config.toml is mostly made of, and
    /// not a longer key that merely starts with this one.</summary>
    private static bool IsAssignmentTo(string line, string key)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith(key, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = trimmed[key.Length..].TrimStart();
        return rest.StartsWith('=');
    }

    private static string TomlAssignment(string key, string value) => $"{key} = \"{EscapeTomlString(value)}\"";

    // TOML basic-string escaping only needs backslashes and double quotes handled for values this
    // constrained (a shared secret and a URL, neither with newlines) — same as
    // packaging/install.sh's own handling of the token.
    private static string EscapeTomlString(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
