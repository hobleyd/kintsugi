using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ScriptApproval;
using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Infrastructure.ScriptApproval;

/// <summary>
/// Reads the corpus of human-approved upgrade scripts out of the shared approval repository's
/// default branch — the trust root for this whole flow, since "approved" means "merged there".
/// </summary>
public class GitHubScriptApprovalSourceClient : IScriptApprovalSourceClient
{
    private readonly HttpClient _httpClient;
    private readonly IGitHubSettingsProvider _settingsProvider;

    public GitHubScriptApprovalSourceClient(HttpClient httpClient, IGitHubSettingsProvider settingsProvider)
    {
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
        ScriptApprovalGitHubHeaders.ApplyStaticHeaders(_httpClient);
    }

    public async Task<ScriptApprovalSourceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        // Read per call, never captured in the constructor: which repository this is and which token
        // reaches it are both editable on the GitHub settings page, so a captured value would ignore
        // every edit until the next restart. See GitHubSettings.
        var settings = await _settingsProvider.GetAsync(cancellationToken);
        var repository = settings.ScriptApprovalRepository;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ScriptApprovalGitHubHeaders.MetadataTimeout);

        string? branch = null;
        try
        {
            branch = await GetDefaultBranchAsync(repository, settings.ApiToken, timeout.Token);
            var head = await GetHeadShaAsync(repository, branch, settings.ApiToken, timeout.Token);
            return new ScriptApprovalSourceStatus(repository, branch, head, UnavailableReason: null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ScriptApprovalSourceStatus(
                repository, branch, HeadCommitSha: null,
                $"Reading {repository} took longer than {ScriptApprovalGitHubHeaders.MetadataTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never thrown onwards: the page renders this reason in place of the upstream state, the
            // same way the Clients page reports an unreachable release listing.
            return new ScriptApprovalSourceStatus(repository, branch, HeadCommitSha: null, ex.Message);
        }
    }

    public async Task<ApprovedScriptCorpusReadResult> GetCorpusAsync(string commitish, CancellationToken cancellationToken)
    {
        var settings = await _settingsProvider.GetAsync(cancellationToken);

        // No MetadataTimeout here — unlike the status check this is an explicit button press, and the
        // archive is the whole repository rather than one JSON document.
        using var request = ScriptApprovalGitHubHeaders.Request(
            HttpMethod.Get, $"https://api.github.com/repos/{settings.ScriptApprovalRepository}/tarball/{commitish}", settings.ApiToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Buffered: TarReader over a GZipStream reads forward only, but the archive is a handful of
        // small text files and holding it in memory keeps the failure modes simple.
        using var buffered = new MemoryStream();
        await response.Content.CopyToAsync(buffered, cancellationToken);
        buffered.Position = 0;

        return ReadCorpus(buffered);
    }

    private async Task<string> GetDefaultBranchAsync(string repository, string? token, CancellationToken cancellationToken)
    {
        using var request = ScriptApprovalGitHubHeaders.Request(HttpMethod.Get, $"https://api.github.com/repos/{repository}", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.TryGetProperty("default_branch", out var branch) && branch.ValueKind == JsonValueKind.String
            ? branch.GetString()!
            : "main";
    }

    private async Task<string?> GetHeadShaAsync(string repository, string branch, string? token, CancellationToken cancellationToken)
    {
        using var request = ScriptApprovalGitHubHeaders.Request(
            HttpMethod.Get, $"https://api.github.com/repos/{repository}/commits/{branch}", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.TryGetProperty("sha", out var sha) && sha.ValueKind == JsonValueKind.String
            ? sha.GetString()
            : null;
    }

    /// <summary>
    /// Pulls every well-formed approval entry out of a repository tarball. A pure function over the
    /// archive bytes so it can be tested against a hand-built one, for the same reason
    /// <c>GitHubAgentPackageSourceClient.ParseLatestReleases</c> takes a raw JSON string and every
    /// agent's package-manager output parser takes a plain <c>&amp;str</c>.
    ///
    /// A malformed entry is skipped with a reason rather than failing the whole import: one bad
    /// directory must not cost every other approved script its refresh, which is the same call
    /// <c>ImportAgentPackagesFromSourceCommandHandler</c> makes per platform.
    /// </summary>
    public static ApprovedScriptCorpusReadResult ReadCorpus(Stream gzipTar)
    {
        var files = ReadArchiveFiles(gzipTar);
        var entries = new List<ApprovedScriptCorpusEntry>();
        var skipped = new List<string>();

        // Group by the sha256 directory name. Ordered so both the entries and any complaints come
        // out in a stable order for the page and for tests.
        var contentDirectories = files.Keys
            .Select(ContentDirectoryOf)
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var sha256 in contentDirectories)
        {
            var prefix = $"{ApprovedScriptCorpus.RootDirectory}/{sha256}/";

            if (!files.TryGetValue(prefix + ApprovedScriptCorpus.MetadataFileName, out var metadataJson))
            {
                skipped.Add($"{sha256}: no {ApprovedScriptCorpus.MetadataFileName}.");
                continue;
            }

            ApprovedScriptMetadataDocument? metadata;
            try
            {
                metadata = ApprovedScriptCorpus.Deserialize<ApprovedScriptMetadataDocument>(metadataJson);
            }
            catch (JsonException ex)
            {
                skipped.Add($"{sha256}: unreadable {ApprovedScriptCorpus.MetadataFileName} — {ex.Message}");
                continue;
            }

            if (metadata is null)
            {
                skipped.Add($"{sha256}: empty {ApprovedScriptCorpus.MetadataFileName}.");
                continue;
            }

            var scriptPath = prefix + $"script{metadata.Language.FileExtension()}";
            if (!files.TryGetValue(scriptPath, out var script))
            {
                skipped.Add($"{sha256}: no script file at {scriptPath}.");
                continue;
            }

            // The directory name is the content's own hash, so this catches an entry whose script was
            // edited in place — and, equally, one that was moved into the wrong directory. Either way
            // the entry no longer describes itself and nothing downstream should trust its metadata.
            var actual = ScriptContentHash.Of(script);
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add($"{sha256}: script hashes to {actual}, so the directory name does not describe its contents.");
                continue;
            }

            if (!string.Equals(metadata.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add($"{sha256}: metadata claims sha256 {metadata.Sha256}.");
                continue;
            }

            var signatures = ReadSignatures(files, prefix, sha256, skipped);
            if (signatures.Count == 0)
            {
                skipped.Add($"{sha256}: no usable signature — nothing vouches for this script.");
                continue;
            }

            entries.Add(new ApprovedScriptCorpusEntry(sha256, metadata, script, signatures));
        }

        return new ApprovedScriptCorpusReadResult(entries, skipped);
    }

    private static List<ApprovedScriptSignatureDocument> ReadSignatures(
        IReadOnlyDictionary<string, string> files, string prefix, string sha256, List<string> skipped)
    {
        var signaturesPrefix = prefix + ApprovedScriptCorpus.SignaturesDirectory + "/";
        var signatures = new List<ApprovedScriptSignatureDocument>();

        foreach (var path in files.Keys
            .Where(p => p.StartsWith(signaturesPrefix, StringComparison.Ordinal) && p.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            ApprovedScriptSignatureDocument? signature;
            try
            {
                signature = ApprovedScriptCorpus.Deserialize<ApprovedScriptSignatureDocument>(files[path]);
            }
            catch (JsonException ex)
            {
                skipped.Add($"{path}: unreadable — {ex.Message}");
                continue;
            }

            if (signature is null || string.IsNullOrWhiteSpace(signature.Signature) || string.IsNullOrWhiteSpace(signature.SignerPublicKeyPem))
            {
                skipped.Add($"{path}: incomplete signature document.");
                continue;
            }

            if (!string.Equals(signature.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add($"{path}: signs {signature.Sha256}, not the {sha256} directory it sits in.");
                continue;
            }

            signatures.Add(signature);
        }

        return signatures;
    }

    /// <summary>
    /// Every regular file in the archive, keyed by its path with GitHub's wrapper directory removed.
    ///
    /// That stripping is essential and silent when wrong: <c>/tarball/{ref}</c> nests everything under
    /// a <c>{owner}-{repo}-{shortsha}/</c> directory, so matching <c>approved-scripts/</c> against the
    /// raw entry names finds nothing at all — which is indistinguishable from an empty corpus.
    /// </summary>
    private static Dictionary<string, string> ReadArchiveFiles(Stream gzipTar)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        using var gzip = new GZipStream(gzipTar, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(gzip, leaveOpen: true);

        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.DataStream is null || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }

            var path = StripWrapperDirectory(entry.Name);
            if (!path.StartsWith(ApprovedScriptCorpus.RootDirectory + "/", StringComparison.Ordinal))
            {
                continue;
            }

            // leaveOpen, because disposing a tar entry's DataStream (a SubReadStream) leaves TarReader
            // unable to advance to the next entry — it throws ObjectDisposedException on the very
            // next GetNextEntry rather than simply returning what it has.
            using var reader = new StreamReader(entry.DataStream, Encoding.UTF8, leaveOpen: true);
            files[path] = reader.ReadToEnd();
        }

        return files;
    }

    private static string StripWrapperDirectory(string entryName)
    {
        var normalized = entryName.StartsWith("./", StringComparison.Ordinal) ? entryName[2..] : entryName;
        var slash = normalized.IndexOf('/');
        return slash < 0 ? normalized : normalized[(slash + 1)..];
    }

    /// <summary>The <c>&lt;sha256&gt;</c> segment of a path under the corpus root, or null if the path
    /// isn't inside one.</summary>
    private static string? ContentDirectoryOf(string path)
    {
        var prefix = ApprovedScriptCorpus.RootDirectory + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = path[prefix.Length..];
        var slash = rest.IndexOf('/');
        return slash <= 0 ? null : rest[..slash];
    }
}
