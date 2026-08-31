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

    // The bundled config.toml ships the placeholder kintsugi.example.com, because a public
    // repository must never carry a real server's address. These cover the import-time rewrite
    // that replaces it — see IAgentPackageArchiveRewriter for why that one happens at import while
    // the enrollment token happens per download.

    [Fact]
    public async Task WithApiBaseUrl_ReplacesTheApiBaseUrlLine_InConfigToml()
    {
        var source = BuildTarGz(("config.toml", "api_base_url = \"https://kintsugi.example.com:8443\"\nenrollment_token = \"\"\n"));

        var result = await _rewriter.WithApiBaseUrl(source, "https://patch.internal:8443", CancellationToken.None);

        var entries = await ReadTarGzEntries(result);
        Assert.Contains("api_base_url = \"https://patch.internal:8443\"", entries["config.toml"]);
        Assert.DoesNotContain("kintsugi.example.com", entries["config.toml"]);
    }

    [Fact]
    public async Task WithApiBaseUrl_ReplacesTheLineWhereItStands_LeavingItUnderItsOwnComment()
    {
        // The comment above this line is the first thing whoever unpacks the tarball reads, and it
        // explains what the value is and what to restart after changing it. Appending the new
        // value at the bottom of the file instead would leave that comment pointing at nothing.
        var config = "# Change api_base_url once the backend has a stable address.\n"
            + "api_base_url = \"https://kintsugi.example.com:8443\"\n"
            + "\n# The enrollment token.\nenrollment_token = \"\"\n";
        var source = BuildTarGz(("config.toml", config));

        var result = await _rewriter.WithApiBaseUrl(source, "https://patch.internal:8443", CancellationToken.None);

        var lines = (await ReadTarGzEntries(result))["config.toml"].Split('\n');
        var index = Array.FindIndex(lines, line => line.StartsWith("api_base_url", StringComparison.Ordinal));
        Assert.Equal(1, index);
        Assert.StartsWith("# Change api_base_url", lines[index - 1]);
    }

    [Fact]
    public async Task WithApiBaseUrl_LeavesCommentedMentionsOfTheKeyAlone()
    {
        var config = "# Change api_base_url once the backend has a stable address.\napi_base_url = \"https://kintsugi.example.com\"\n";
        var source = BuildTarGz(("config.toml", config));

        var result = await _rewriter.WithApiBaseUrl(source, "https://patch.internal:8443", CancellationToken.None);

        var entries = await ReadTarGzEntries(result);
        Assert.Contains("# Change api_base_url once the backend has a stable address.", entries["config.toml"]);
    }

    [Fact]
    public async Task WithApiBaseUrl_ConfigWithNoSuchLine_AppendsIt()
    {
        var source = BuildTarGz(("config.toml", "enrollment_token = \"\"\n"));

        var result = await _rewriter.WithApiBaseUrl(source, "https://patch.internal:8443", CancellationToken.None);

        var entries = await ReadTarGzEntries(result);
        Assert.Contains("api_base_url = \"https://patch.internal:8443\"", entries["config.toml"]);
    }

    [Fact]
    public async Task WithApiBaseUrl_LeavesOtherEntriesByteForByteUnchanged()
    {
        var source = BuildTarGz(("kintsugi-agent", "not really a binary"), ("config.toml", "api_base_url = \"https://kintsugi.example.com\"\n"));

        var result = await _rewriter.WithApiBaseUrl(source, "https://patch.internal:8443", CancellationToken.None);

        var entries = await ReadTarGzEntries(result);
        Assert.Equal("not really a binary", entries["kintsugi-agent"]);
    }

    [Fact]
    public async Task WithApiBaseUrl_ArchiveWithNoConfigEntry_LeavesEveryEntryUnchanged()
    {
        var source = BuildTarGz(("kintsugi-agent", "binary contents"), ("install.sh", "#!/bin/bash\necho hi\n"));

        var result = await _rewriter.WithApiBaseUrl(source, "https://patch.internal:8443", CancellationToken.None);

        var entries = await ReadTarGzEntries(result);
        Assert.Equal("binary contents", entries["kintsugi-agent"]);
        Assert.Equal("#!/bin/bash\necho hi\n", entries["install.sh"]);
    }

    [Fact]
    public async Task ImportThenDownload_LeavesExactlyOneOfEachRewrittenKey()
    {
        // The real sequence: WithApiBaseUrl runs once when the build is imported off GitHub, and
        // WithEnrollmentToken runs again over that same archive on every anonymous download. The
        // two passes must not disturb each other's line, or the agent gets a config.toml with a
        // duplicated key — which some TOML parsers resolve silently and others reject outright.
        var config = "api_base_url = \"https://kintsugi.example.com:8443\"\nenrollment_token = \"\"\n";
        var imported = await _rewriter.WithApiBaseUrl(
            BuildTarGz(("config.toml", config)), "https://patch.internal:8443", CancellationToken.None);

        var downloaded = await _rewriter.WithEnrollmentToken(imported, "the-current-token", CancellationToken.None);

        var lines = (await ReadTarGzEntries(downloaded))["config.toml"].Split('\n');
        Assert.Single(lines, line => line.StartsWith("api_base_url", StringComparison.Ordinal));
        Assert.Single(lines, line => line.StartsWith("enrollment_token", StringComparison.Ordinal));
        Assert.Contains("api_base_url = \"https://patch.internal:8443\"", lines);
        Assert.Contains("enrollment_token = \"the-current-token\"", lines);
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
