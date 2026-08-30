using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Kintsugi.Infrastructure.Storage;

namespace Kintsugi.Tests.Infrastructure;

public class AgentPackageArchiveRewriterTests
{
    private readonly AgentPackageArchiveRewriter _rewriter = new();

    [Fact]
    public async Task WithEnrollmentToken_ReplacesTheEnrollmentTokenLine_InConfigToml()
    {
        var source = BuildTarGz(("config.toml", "api_base_url = \"https://example.com\"\nenrollment_token = \"\"\n"));

        var result = await _rewriter.WithEnrollmentToken(source, "new-token", CancellationToken.None);

        var entries = await ReadTarGzEntries(result);
        Assert.Contains("enrollment_token = \"new-token\"", entries["config.toml"]);
        Assert.Contains("api_base_url = \"https://example.com\"", entries["config.toml"]);
    }

    [Fact]
    public async Task WithEnrollmentToken_LeavesOtherEntriesByteForByteUnchanged()
    {
        var source = BuildTarGz(("kintsugi-agent", "not really a binary"), ("config.toml", "enrollment_token = \"\"\n"));

        var result = await _rewriter.WithEnrollmentToken(source, "abc123", CancellationToken.None);

        var entries = await ReadTarGzEntries(result);
        Assert.Equal("not really a binary", entries["kintsugi-agent"]);
    }

    [Fact]
    public async Task WithEnrollmentToken_NullToken_WritesABlankValue()
    {
        var source = BuildTarGz(("config.toml", "enrollment_token = \"stale-token\"\n"));

        var result = await _rewriter.WithEnrollmentToken(source, null, CancellationToken.None);

        var entries = await ReadTarGzEntries(result);
        Assert.Contains("enrollment_token = \"\"", entries["config.toml"]);
        Assert.DoesNotContain("stale-token", entries["config.toml"]);
    }

    [Fact]
    public async Task WithEnrollmentToken_EscapesBackslashesAndQuotesInTheToken()
    {
        var source = BuildTarGz(("config.toml", "enrollment_token = \"\"\n"));

        var result = await _rewriter.WithEnrollmentToken(source, "weird\"token\\value", CancellationToken.None);

        var entries = await ReadTarGzEntries(result);
        Assert.Contains("enrollment_token = \"weird\\\"token\\\\value\"", entries["config.toml"]);
    }

    [Fact]
    public async Task WithEnrollmentToken_ArchiveWithNoConfigEntry_LeavesEveryEntryUnchanged()
    {
        var source = BuildTarGz(("kintsugi-agent", "binary contents"), ("install.sh", "#!/bin/bash\necho hi\n"));

        var result = await _rewriter.WithEnrollmentToken(source, "irrelevant", CancellationToken.None);

        var entries = await ReadTarGzEntries(result);
        Assert.Equal("binary contents", entries["kintsugi-agent"]);
        Assert.Equal("#!/bin/bash\necho hi\n", entries["install.sh"]);
    }

    private static MemoryStream BuildTarGz(params (string Name, string Content)[] entries)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
                };
                writer.WriteEntry(entry);
            }
        }

        output.Position = 0;
        return output;
    }

    private static async Task<Dictionary<string, string>> ReadTarGzEntries(Stream tarGz)
    {
        var result = new Dictionary<string, string>();
        await using var gzip = new GZipStream(tarGz, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.DataStream is null)
            {
                continue;
            }

            // leaveOpen: true — TarReader tracks its own reference to this same underlying stream
            // to know how far to skip on the *next* GetNextEntry() call; letting StreamReader's
            // Dispose() close it out from under that bookkeeping throws ObjectDisposedException on
            // the following iteration.
            using var streamReader = new StreamReader(entry.DataStream, leaveOpen: true);
            result[entry.Name] = await streamReader.ReadToEndAsync();
        }

        return result;
    }
}
