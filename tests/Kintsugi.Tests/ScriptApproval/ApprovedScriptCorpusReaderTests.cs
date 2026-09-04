using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Kintsugi.Application.ScriptApproval;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Infrastructure.ScriptApproval;

namespace Kintsugi.Tests.ScriptApproval;

/// <summary>
/// Exercises the corpus reader against archives built here, rather than against whatever GitHub
/// happens to be serving — the same reason <c>GitHubAgentPackageSourceClient.ParseReleases</c>
/// takes a raw JSON string and every agent's package-manager parser takes a plain string.
/// </summary>
public class ApprovedScriptCorpusReaderTests
{
    private const string Script = "#!/bin/bash\nbrew upgrade \"$2\"\n";
    private const string Fingerprint = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public void ReadCorpus_ReadsAnEntryWrittenByThePublisher_RoundTrip()
    {
        var result = GitHubScriptApprovalSourceClient.ReadCorpus(BuildArchive(StandardEntry()));

        var entry = Assert.Single(result.Entries);
        Assert.Empty(result.SkippedReasons);
        // Byte-for-byte, including the trailing newline — anything else changes the hash and
        // invalidates every signature over it.
        Assert.Equal(Script, entry.Script);
        Assert.Equal(ScriptContentHash.Of(Script), entry.Sha256);
        Assert.Equal("pm:Homebrew", entry.Metadata.PlatformBucket);
        Assert.Equal(ScriptLanguage.Bash, entry.Metadata.Language);
        Assert.Equal(Fingerprint, Assert.Single(entry.Signatures).SignerFingerprint);
    }

    [Fact]
    public void ReadCorpus_StripsGitHubsWrapperDirectory()
    {
        // /tarball/{ref} nests everything under {owner}-{repo}-{shortsha}/. Getting this wrong finds
        // nothing at all, which is indistinguishable from an empty corpus — hence a test of its own.
        var archive = BuildArchive(StandardEntry(), wrapperDirectory: "hobleyd-kintsugi-4f2c1ab");

        Assert.Single(GitHubScriptApprovalSourceClient.ReadCorpus(archive).Entries);
    }

    [Fact]
    public void ReadCorpus_WhenTheScriptWasEditedInPlace_SkipsTheEntry()
    {
        var files = StandardEntry();
        var scriptPath = files.Keys.Single(k => k.EndsWith(".sh", StringComparison.Ordinal));
        files[scriptPath] = Script + "rm -rf /\n";

        var result = GitHubScriptApprovalSourceClient.ReadCorpus(BuildArchive(files));

        // The directory name is the content's own hash, so an edit in place no longer describes
        // itself and nothing downstream should trust its metadata.
        Assert.Empty(result.Entries);
        Assert.Contains(result.SkippedReasons, r => r.Contains("does not describe its contents"));
    }

    [Fact]
    public void ReadCorpus_WhenNothingVouchesForTheScript_SkipsTheEntry()
    {
        var files = StandardEntry();
        foreach (var path in files.Keys.Where(k => k.Contains("/signatures/", StringComparison.Ordinal)).ToList())
        {
            files.Remove(path);
        }

        var result = GitHubScriptApprovalSourceClient.ReadCorpus(BuildArchive(files));

        Assert.Empty(result.Entries);
        Assert.Contains(result.SkippedReasons, r => r.Contains("nothing vouches for this script"));
    }

    [Fact]
    public void ReadCorpus_WhenASignatureSitsInTheWrongDirectory_SkipsThatSignature()
    {
        var files = StandardEntry();
        var signaturePath = files.Keys.Single(k => k.Contains("/signatures/", StringComparison.Ordinal));
        files[signaturePath] = ApprovedScriptCorpus.Serialize(new ApprovedScriptSignatureDocument(
            ScriptContentHash.Of("something else entirely"), Fingerprint, "-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----\n",
            "c2ln", "someone@example.invalid", DateTimeOffset.UnixEpoch));

        var result = GitHubScriptApprovalSourceClient.ReadCorpus(BuildArchive(files));

        Assert.Empty(result.Entries);
        Assert.Contains(result.SkippedReasons, r => r.Contains("not the", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadCorpus_IgnoresEverythingOutsideTheCorpusRoot()
    {
        var files = StandardEntry();
        files["README.md"] = "# kintsugi\n";
        files["src/Kintsugi.Domain/Entities/UpgradePath.cs"] = "// not an approval\n";

        var result = GitHubScriptApprovalSourceClient.ReadCorpus(BuildArchive(files));

        // The approval repository is a normal repository that happens to carry a corpus, so
        // unrelated files are not a problem to report.
        Assert.Single(result.Entries);
        Assert.Empty(result.SkippedReasons);
    }

    [Fact]
    public void ReadCorpus_StillReadsAnEntryNamedScriptSh_FromBeforeTheNameWasDescriptive()
    {
        // Every entry approved before ApprovedScriptIdentity existed carries the fixed `script.sh`
        // name. The reader finds the script by extension precisely so those keep importing — a
        // rename on the trust root would invalidate nothing but would be churn on reviewed content.
        var result = GitHubScriptApprovalSourceClient.ReadCorpus(
            BuildArchive(StandardEntry(ApprovedScriptCorpus.LegacyScriptBaseName)));

        var entry = Assert.Single(result.Entries);
        Assert.Empty(result.SkippedReasons);
        Assert.Equal(Script, entry.Script);
    }

    [Fact]
    public void ReadCorpus_WithBothTheOldAndNewNamePresent_ReadsTheEntryOnce()
    {
        // What a re-approval of already-published bytes can leave behind. Both files hold the same
        // content — the directory name is that content's hash — so either satisfies the entry, and
        // the reader must not report the directory as ambiguous or read it twice.
        var files = StandardEntry();
        files[ApprovedScriptCorpus.ScriptPath(
            ScriptContentHash.Of(Script), ApprovedScriptCorpus.LegacyScriptBaseName, ScriptLanguage.Bash)] = Script;

        var result = GitHubScriptApprovalSourceClient.ReadCorpus(BuildArchive(files));

        var entry = Assert.Single(result.Entries);
        Assert.Empty(result.SkippedReasons);
        Assert.Equal(Script, entry.Script);
    }

    [Fact]
    public void ReadCorpus_IgnoresASignatureFileWhenLookingForTheScript()
    {
        // signatures/<fingerprint>.json sits inside the same content directory, so a discovery that
        // matched on prefix alone could pick it up if a language's extension ever collided.
        var result = GitHubScriptApprovalSourceClient.ReadCorpus(BuildArchive(StandardEntry()));

        Assert.Equal(Script, Assert.Single(result.Entries).Script);
    }

    /// <summary>One well-formed entry, laid out exactly where <c>ApprovedScriptCorpus</c> says it goes
    /// — built through those same helpers so a change to the layout breaks the test rather than
    /// silently diverging from it.</summary>
    private static Dictionary<string, string> StandardEntry(string scriptBaseName = "homebrew")
    {
        var sha256 = ScriptContentHash.Of(Script);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ApprovedScriptCorpus.ScriptPath(sha256, scriptBaseName, ScriptLanguage.Bash)] = Script,
            [ApprovedScriptCorpus.MetadataPath(sha256)] = ApprovedScriptCorpus.Serialize(
                new ApprovedScriptMetadataDocument(sha256, "pm:Homebrew", ScriptLanguage.Bash, "Firefox", "firefox")),
            [ApprovedScriptCorpus.SignaturePath(sha256, Fingerprint)] = ApprovedScriptCorpus.Serialize(
                new ApprovedScriptSignatureDocument(
                    sha256, Fingerprint, "-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----\n",
                    "c2ln", "someone@example.invalid", DateTimeOffset.UnixEpoch)),
        };
    }

    private static MemoryStream BuildArchive(Dictionary<string, string> files, string? wrapperDirectory = null)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, $"{wrapperDirectory ?? "repo-main"}/{path}")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                };
                tar.WriteEntry(entry);
            }
        }

        output.Position = 0;
        return output;
    }
}
