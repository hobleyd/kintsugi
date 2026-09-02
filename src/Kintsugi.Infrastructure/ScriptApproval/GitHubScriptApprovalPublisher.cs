using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ScriptApproval;
using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Infrastructure.ScriptApproval;

/// <summary>
/// Publishes a human's script approval to the shared approval repository as a pull request.
/// </summary>
/// <remarks>
/// The work is deliberately shaped to be idempotent, because signing the same script twice is a
/// normal thing for a reviewer to do (a re-review, a second look after an edit elsewhere) and it must
/// not litter the repository with duplicate pull requests. Three checks, in order of cheapness:
/// this signer's attestation is already on the default branch; a branch proposing it already exists;
/// a file about to be written already holds exactly these bytes. Only what is genuinely new gets
/// written.
///
/// The branch name is derived from the content hash and the signer fingerprint rather than being
/// unique per attempt, which is what makes the second check possible at all.
/// </remarks>
public class GitHubScriptApprovalPublisher : IScriptApprovalPublisher
{
    /// <summary>Which repository this call is writing to and with what credential. Threaded through
    /// every step of one publish rather than held on the instance, so a settings-page edit lands on
    /// the next publish instead of the next restart — see <c>GitHubSettings</c>.</summary>
    private record GitHubTarget(string Repository, string Token)
    {
        public string Owner => Repository.Split('/')[0];
    }

    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubScriptApprovalPublisher> _logger;
    private readonly IGitHubSettingsProvider _settingsProvider;

    public GitHubScriptApprovalPublisher(
        HttpClient httpClient, IGitHubSettingsProvider settingsProvider, ILogger<GitHubScriptApprovalPublisher> logger)
    {
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
        _logger = logger;
        ScriptApprovalGitHubHeaders.ApplyStaticHeaders(_httpClient);
    }

    public async Task<ScriptApprovalPublishResult> PublishAsync(
        ScriptApprovalSubmission submission, CancellationToken cancellationToken)
    {
        var settings = await _settingsProvider.GetAsync(cancellationToken);
        if (settings.ScriptApprovalToken is null)
        {
            return new ScriptApprovalPublishResult(
                ScriptApprovalPublishOutcome.Disabled,
                Message: "No script-approval token is configured on the GitHub settings page, so this approval was "
                    + $"recorded on this server only and not proposed to {settings.ScriptApprovalRepository}.");
        }

        var target = new GitHubTarget(settings.ScriptApprovalRepository, settings.ScriptApprovalToken);

        try
        {
            return await PublishCoreAsync(target, submission, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reported, never rethrown. The local approval has already been saved by the time this
            // runs, and a GitHub outage must not be able to stop a script a human reviewed from
            // patching the fleet it was reviewed for — that is what "the pull request is a record,
            // not a gate" means in practice.
            _logger.LogWarning(ex, "Failed to publish the approval of {Application} to {Repository}.",
                submission.ApplicationName, target.Repository);
            return new ScriptApprovalPublishResult(ScriptApprovalPublishOutcome.Failed, Message: ex.Message);
        }
    }

    private async Task<ScriptApprovalPublishResult> PublishCoreAsync(
        GitHubTarget target, ScriptApprovalSubmission submission, CancellationToken cancellationToken)
    {
        var defaultBranch = await GetDefaultBranchAsync(target, cancellationToken);

        // What this entry is, as opposed to which row was signed to produce it — the two differ for
        // every package-manager script. See ApprovedScriptIdentity.
        var identity = ApprovedScriptIdentity.For(submission);
        var scriptPath = await ResolveScriptPathAsync(target, submission, identity, defaultBranch, cancellationToken);
        var files = BuildFiles(submission, identity, scriptPath);
        var signaturePath = ApprovedScriptCorpus.SignaturePath(submission.Sha256, submission.SignerFingerprint);

        // Already merged: this signer has vouched for these exact bytes on the trust root, so there
        // is nothing left to propose.
        var existingSignature = await GetFileAsync(target, signaturePath, defaultBranch, cancellationToken);
        if (existingSignature is not null && existingSignature.Content == files[signaturePath])
        {
            return new ScriptApprovalPublishResult(
                ScriptApprovalPublishOutcome.AlreadyApproved,
                Message: $"{target.Repository} already carries this signature on {defaultBranch}.");
        }

        var branch = BranchNameFor(submission);

        // Already proposed: reuse the open pull request rather than opening a second one that would
        // say the same thing.
        var openPullRequest = await FindOpenPullRequestAsync(target, branch, cancellationToken);
        if (openPullRequest is not null)
        {
            return new ScriptApprovalPublishResult(ScriptApprovalPublishOutcome.PullRequestAlreadyOpen, openPullRequest);
        }

        await EnsureBranchAsync(target, branch, defaultBranch, cancellationToken);

        var wrote = false;
        foreach (var (path, content) in files)
        {
            wrote |= await PutFileAsync(target, path, content, branch, submission, identity, cancellationToken);
        }

        if (!wrote)
        {
            // Every file was already present with identical content on the branch, which means a
            // previous attempt wrote them and the pull request was closed without merging. Opening a
            // new one would have nothing to show, so say so rather than failing.
            return new ScriptApprovalPublishResult(
                ScriptApprovalPublishOutcome.AlreadyApproved,
                Message: $"Branch {branch} in {target.Repository} already carries this approval unchanged.");
        }

        var url = await OpenPullRequestAsync(target, branch, defaultBranch, submission, identity, cancellationToken);
        return new ScriptApprovalPublishResult(ScriptApprovalPublishOutcome.PullRequestOpened, url);
    }

    /// <summary>
    /// The path this entry's script is written to: the descriptive name from
    /// <paramref name="identity"/>, unless the entry already carries the original fixed
    /// <c>script.sh</c>/<c>script.ps1</c> name on <paramref name="defaultBranch"/> with exactly
    /// these bytes — in which case that existing file <em>is</em> the script and is written to
    /// again, rather than leaving the same content in the directory twice under two names.
    /// </summary>
    private async Task<string> ResolveScriptPathAsync(
        GitHubTarget target,
        ScriptApprovalSubmission submission,
        ApprovedScriptIdentity identity,
        string defaultBranch,
        CancellationToken cancellationToken)
    {
        var descriptive = ApprovedScriptCorpus.ScriptPath(submission.Sha256, identity.FileBaseName, submission.Language);
        var legacy = ApprovedScriptCorpus.ScriptPath(
            submission.Sha256, ApprovedScriptCorpus.LegacyScriptBaseName, submission.Language);
        if (string.Equals(descriptive, legacy, StringComparison.Ordinal))
        {
            return descriptive;
        }

        var existing = await GetFileAsync(target, legacy, defaultBranch, cancellationToken);
        return existing is not null && existing.Content == submission.Script ? legacy : descriptive;
    }

    /// <summary>
    /// The three files one approval consists of, keyed by repository path. Built together so the
    /// script, its metadata and the signature over it are always written as one consistent set.
    /// </summary>
    private static Dictionary<string, string> BuildFiles(
        ScriptApprovalSubmission submission, ApprovedScriptIdentity identity, string scriptPath)
    {
        var metadata = new ApprovedScriptMetadataDocument(
            submission.Sha256,
            submission.PlatformBucket,
            submission.Language,
            identity.DisplayName,
            identity.ApplicationIdentifier);

        var signature = new ApprovedScriptSignatureDocument(
            submission.Sha256,
            submission.SignerFingerprint,
            submission.SignerPublicKeyPem,
            submission.Signature,
            submission.SignedBy,
            submission.SignedAtUtc);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The script's bytes exactly as signed — no trailing newline added, no re-indentation,
            // nothing. Any change here changes its hash and invalidates every signature over it.
            [scriptPath] = submission.Script,
            [ApprovedScriptCorpus.MetadataPath(submission.Sha256)] = ApprovedScriptCorpus.Serialize(metadata),
            [ApprovedScriptCorpus.SignaturePath(submission.Sha256, submission.SignerFingerprint)] =
                ApprovedScriptCorpus.Serialize(signature),
        };
    }

    /// <summary>Derived from content and signer, not from the moment of publishing, so a second
    /// attempt at the same approval finds its own earlier branch instead of creating another.
    /// Truncated because a branch name carrying two full SHA-256s is unreadable in a pull request
    /// list, and 12 hex characters of each is already far past collision territory here.</summary>
    private static string BranchNameFor(ScriptApprovalSubmission submission) =>
        $"script-approval/{submission.Sha256[..12]}-{ScriptSignerFingerprint.Bare(submission.SignerFingerprint)[..12]}";

    private async Task<string> GetDefaultBranchAsync(GitHubTarget target, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(target, HttpMethod.Get, $"https://api.github.com/repos/{target.Repository}", cancellationToken);
        await EnsureSuccessAsync(response, $"read {target.Repository}", cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.TryGetProperty("default_branch", out var branch) && branch.ValueKind == JsonValueKind.String
            ? branch.GetString()!
            : "main";
    }

    private record ExistingFile(string Sha, string Content);

    /// <summary>One file's current blob sha and decoded text on <paramref name="reference"/>, or null
    /// when it isn't there. The sha is what a later write has to quote to update it rather than
    /// being rejected as a conflicting create.</summary>
    private async Task<ExistingFile?> GetFileAsync(GitHubTarget target, string path, string reference, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            target, HttpMethod.Get,
            $"https://api.github.com/repos/{target.Repository}/contents/{path}?ref={Uri.EscapeDataString(reference)}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, $"read {path}", cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;

        if (!root.TryGetProperty("sha", out var sha) || sha.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var content = root.TryGetProperty("content", out var encoded) && encoded.ValueKind == JsonValueKind.String
            // GitHub wraps base64 content at 60 characters, which Convert.FromBase64String rejects
            // unless the newlines are stripped first.
            ? Encoding.UTF8.GetString(Convert.FromBase64String(encoded.GetString()!.Replace("\n", string.Empty)))
            : string.Empty;

        return new ExistingFile(sha.GetString()!, content);
    }

    private async Task<string?> FindOpenPullRequestAsync(GitHubTarget target, string branch, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            target, HttpMethod.Get,
            $"https://api.github.com/repos/{target.Repository}/pulls?state=open&head={Uri.EscapeDataString($"{target.Owner}:{branch}")}",
            cancellationToken);
        await EnsureSuccessAsync(response, "list open pull requests", cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var pull in document.RootElement.EnumerateArray())
        {
            if (pull.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String)
            {
                return url.GetString();
            }
        }

        return null;
    }

    private async Task EnsureBranchAsync(GitHubTarget target, string branch, string baseBranch, CancellationToken cancellationToken)
    {
        using var existing = await SendAsync(
            target, HttpMethod.Get, $"https://api.github.com/repos/{target.Repository}/git/ref/heads/{branch}", cancellationToken);
        if (existing.IsSuccessStatusCode)
        {
            // Left where it is rather than reset to the base branch: whatever is on it was written by
            // an earlier attempt at this same approval, and the per-file comparison below sorts out
            // what still needs writing.
            return;
        }

        using var baseRef = await SendAsync(
            target, HttpMethod.Get, $"https://api.github.com/repos/{target.Repository}/git/ref/heads/{baseBranch}", cancellationToken);
        await EnsureSuccessAsync(baseRef, $"read the {baseBranch} ref", cancellationToken);

        using var document = JsonDocument.Parse(await baseRef.Content.ReadAsStringAsync(cancellationToken));
        var sha = document.RootElement.GetProperty("object").GetProperty("sha").GetString()!;

        using var created = await SendJsonAsync(
            target, HttpMethod.Post, $"https://api.github.com/repos/{target.Repository}/git/refs",
            new { @ref = $"refs/heads/{branch}", sha },
            cancellationToken);
        await EnsureSuccessAsync(created, $"create branch {branch}", cancellationToken);
    }

    /// <summary>Writes one file, unless the branch already holds exactly these bytes. Returns whether
    /// anything actually changed, which is how the caller knows whether a pull request would have
    /// anything to show.</summary>
    private async Task<bool> PutFileAsync(
        GitHubTarget target, string path, string content, string branch, ScriptApprovalSubmission submission,
        ApprovedScriptIdentity identity, CancellationToken cancellationToken)
    {
        var existing = await GetFileAsync(target, path, branch, cancellationToken);
        if (existing is not null && existing.Content == content)
        {
            return false;
        }

        using var response = await SendJsonAsync(
            target, HttpMethod.Put, $"https://api.github.com/repos/{target.Repository}/contents/{path}",
            new
            {
                message = $"Approve the {identity.DisplayName} upgrade script for {submission.PlatformBucket}",
                content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
                branch,
                sha = existing?.Sha,
            },
            cancellationToken);
        await EnsureSuccessAsync(response, $"write {path}", cancellationToken);
        return true;
    }

    private async Task<string?> OpenPullRequestAsync(
        GitHubTarget target, string branch, string baseBranch, ScriptApprovalSubmission submission,
        ApprovedScriptIdentity identity, CancellationToken cancellationToken)
    {
        using var response = await SendJsonAsync(
            target, HttpMethod.Post, $"https://api.github.com/repos/{target.Repository}/pulls",
            new
            {
                title = $"Approve the {identity.DisplayName} upgrade script ({submission.PlatformBucket})",
                head = branch,
                @base = baseBranch,
                body = BuildPullRequestBody(submission, identity),
            },
            cancellationToken);
        await EnsureSuccessAsync(response, "open the pull request", cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String
            ? url.GetString()
            : null;
    }

    /// <summary>
    /// What a reviewer sees. Says plainly that merging is what makes the script adoptable by other
    /// servers, because that is the one consequence of the merge that isn't obvious from the diff.
    /// </summary>
    private static string BuildPullRequestBody(ScriptApprovalSubmission submission, ApprovedScriptIdentity identity)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"`{identity.DisplayName}` on `{submission.PlatformBucket}` "
            + $"({submission.Language.Interpreter()}), signed and reviewed on one Kintsugi server.");
        builder.AppendLine();
        if (identity.IsPackageManagerScript)
        {
            // Said out loud because it changes what the reviewer is being asked to weigh: not
            // "is this right for ada-url" but "is this right for everything this manager handles".
            builder.AppendLine("This is the script that manager's applications all share — it takes the "
                + "application from `--appName`/`--appId` at runtime and bakes nothing in, so reviewing it "
                + "once covers every application the manager handles rather than any single one.");
            builder.AppendLine();
        }
        builder.AppendLine($"- Script SHA-256: `{submission.Sha256}`");
        builder.AppendLine($"- Signer fingerprint: `{submission.SignerFingerprint}`");
        builder.AppendLine($"- Signed by: {submission.SignedBy ?? "unrecorded"}");
        builder.AppendLine($"- Signed at: {submission.SignedAtUtc:u}");
        builder.AppendLine();
        builder.AppendLine("Merging this places the script on the default branch, which is the trust root: "
            + "every other Kintsugi server reading this repository may then adopt it, re-sign it with "
            + "its own artifact-signing key, and have its agents run it. Review the script itself, not "
            + "only the metadata.");
        return builder.ToString();
    }

    /// <summary>Every call carries the token on the request itself rather than on the client's
    /// DefaultRequestHeaders: a typed HttpClient instance outlives one publish, and pinning an
    /// Authorization header to it would keep using whatever token was current the first time.</summary>
    private Task<HttpResponseMessage> SendAsync(GitHubTarget target, HttpMethod method, string url, CancellationToken cancellationToken)
    {
        using var request = ScriptApprovalGitHubHeaders.Request(method, url, target.Token);
        return _httpClient.SendAsync(request, cancellationToken);
    }

    private Task<HttpResponseMessage> SendJsonAsync<T>(
        GitHubTarget target, HttpMethod method, string url, T body, CancellationToken cancellationToken)
    {
        using var request = ScriptApprovalGitHubHeaders.Request(method, url, target.Token);
        request.Content = JsonContent.Create(body);
        return _httpClient.SendAsync(request, cancellationToken);
    }

    /// <summary>GitHub's error bodies carry the actual reason (a missing scope, a protected branch, a
    /// stale sha) and <c>EnsureSuccessStatusCode</c> throws all of them away. This one keeps it —
    /// these messages end up in front of whoever just pressed Sign.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"Could not {what}: {(int)response.StatusCode} {response.ReasonPhrase}. {Summarize(body)}".TrimEnd());
    }

    private static string Summarize(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return body.Length <= 200 ? body : body[..200];
        }
    }
}
