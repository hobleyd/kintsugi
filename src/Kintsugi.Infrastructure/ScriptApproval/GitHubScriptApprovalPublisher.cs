using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubScriptApprovalPublisher> _logger;
    private readonly string _repository;
    private readonly string? _token;

    public GitHubScriptApprovalPublisher(
        HttpClient httpClient, IConfiguration configuration, ILogger<GitHubScriptApprovalPublisher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _repository = ScriptApprovalRepository.Resolve(configuration);
        _token = ScriptApprovalRepository.ResolveToken(configuration);
        ScriptApprovalGitHubHeaders.Apply(_httpClient, _token);
    }

    public string RepositoryDescription => _repository;

    public bool IsEnabled => _token is not null;

    private string Owner => _repository.Split('/')[0];

    public async Task<ScriptApprovalPublishResult> PublishAsync(
        ScriptApprovalSubmission submission, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return new ScriptApprovalPublishResult(
                ScriptApprovalPublishOutcome.Disabled,
                Message: $"No {ScriptApprovalRepository.TokenConfigurationKey} is configured, so this approval was "
                    + $"recorded on this server only and not proposed to {_repository}.");
        }

        try
        {
            return await PublishCoreAsync(submission, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reported, never rethrown. The local approval has already been saved by the time this
            // runs, and a GitHub outage must not be able to stop a script a human reviewed from
            // patching the fleet it was reviewed for — that is what "the pull request is a record,
            // not a gate" means in practice.
            _logger.LogWarning(ex, "Failed to publish the approval of {Application} to {Repository}.",
                submission.ApplicationName, _repository);
            return new ScriptApprovalPublishResult(ScriptApprovalPublishOutcome.Failed, Message: ex.Message);
        }
    }

    private async Task<ScriptApprovalPublishResult> PublishCoreAsync(
        ScriptApprovalSubmission submission, CancellationToken cancellationToken)
    {
        var defaultBranch = await GetDefaultBranchAsync(cancellationToken);
        var files = BuildFiles(submission);
        var signaturePath = ApprovedScriptCorpus.SignaturePath(submission.Sha256, submission.SignerFingerprint);

        // Already merged: this signer has vouched for these exact bytes on the trust root, so there
        // is nothing left to propose.
        var existingSignature = await GetFileAsync(signaturePath, defaultBranch, cancellationToken);
        if (existingSignature is not null && existingSignature.Content == files[signaturePath])
        {
            return new ScriptApprovalPublishResult(
                ScriptApprovalPublishOutcome.AlreadyApproved,
                Message: $"{_repository} already carries this signature on {defaultBranch}.");
        }

        var branch = BranchNameFor(submission);

        // Already proposed: reuse the open pull request rather than opening a second one that would
        // say the same thing.
        var openPullRequest = await FindOpenPullRequestAsync(branch, cancellationToken);
        if (openPullRequest is not null)
        {
            return new ScriptApprovalPublishResult(ScriptApprovalPublishOutcome.PullRequestAlreadyOpen, openPullRequest);
        }

        await EnsureBranchAsync(branch, defaultBranch, cancellationToken);

        var wrote = false;
        foreach (var (path, content) in files)
        {
            wrote |= await PutFileAsync(path, content, branch, submission, cancellationToken);
        }

        if (!wrote)
        {
            // Every file was already present with identical content on the branch, which means a
            // previous attempt wrote them and the pull request was closed without merging. Opening a
            // new one would have nothing to show, so say so rather than failing.
            return new ScriptApprovalPublishResult(
                ScriptApprovalPublishOutcome.AlreadyApproved,
                Message: $"Branch {branch} in {_repository} already carries this approval unchanged.");
        }

        var url = await OpenPullRequestAsync(branch, defaultBranch, submission, cancellationToken);
        return new ScriptApprovalPublishResult(ScriptApprovalPublishOutcome.PullRequestOpened, url);
    }

    /// <summary>
    /// The three files one approval consists of, keyed by repository path. Built together so the
    /// script, its metadata and the signature over it are always written as one consistent set.
    /// </summary>
    private static Dictionary<string, string> BuildFiles(ScriptApprovalSubmission submission)
    {
        var metadata = new ApprovedScriptMetadataDocument(
            submission.Sha256,
            submission.PlatformBucket,
            submission.Language,
            submission.ApplicationName,
            submission.ApplicationIdentifier);

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
            [ApprovedScriptCorpus.ScriptPath(submission.Sha256, submission.Language)] = submission.Script,
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

    private async Task<string> GetDefaultBranchAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"https://api.github.com/repos/{_repository}", cancellationToken);
        await EnsureSuccessAsync(response, $"read {_repository}", cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.TryGetProperty("default_branch", out var branch) && branch.ValueKind == JsonValueKind.String
            ? branch.GetString()!
            : "main";
    }

    private record ExistingFile(string Sha, string Content);

    /// <summary>One file's current blob sha and decoded text on <paramref name="reference"/>, or null
    /// when it isn't there. The sha is what a later write has to quote to update it rather than
    /// being rejected as a conflicting create.</summary>
    private async Task<ExistingFile?> GetFileAsync(string path, string reference, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"https://api.github.com/repos/{_repository}/contents/{path}?ref={Uri.EscapeDataString(reference)}",
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

    private async Task<string?> FindOpenPullRequestAsync(string branch, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"https://api.github.com/repos/{_repository}/pulls?state=open&head={Uri.EscapeDataString($"{Owner}:{branch}")}",
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

    private async Task EnsureBranchAsync(string branch, string baseBranch, CancellationToken cancellationToken)
    {
        using var existing = await _httpClient.GetAsync(
            $"https://api.github.com/repos/{_repository}/git/ref/heads/{branch}", cancellationToken);
        if (existing.IsSuccessStatusCode)
        {
            // Left where it is rather than reset to the base branch: whatever is on it was written by
            // an earlier attempt at this same approval, and the per-file comparison below sorts out
            // what still needs writing.
            return;
        }

        using var baseRef = await _httpClient.GetAsync(
            $"https://api.github.com/repos/{_repository}/git/ref/heads/{baseBranch}", cancellationToken);
        await EnsureSuccessAsync(baseRef, $"read the {baseBranch} ref", cancellationToken);

        using var document = JsonDocument.Parse(await baseRef.Content.ReadAsStringAsync(cancellationToken));
        var sha = document.RootElement.GetProperty("object").GetProperty("sha").GetString()!;

        using var created = await _httpClient.PostAsJsonAsync(
            $"https://api.github.com/repos/{_repository}/git/refs",
            new { @ref = $"refs/heads/{branch}", sha },
            cancellationToken);
        await EnsureSuccessAsync(created, $"create branch {branch}", cancellationToken);
    }

    /// <summary>Writes one file, unless the branch already holds exactly these bytes. Returns whether
    /// anything actually changed, which is how the caller knows whether a pull request would have
    /// anything to show.</summary>
    private async Task<bool> PutFileAsync(
        string path, string content, string branch, ScriptApprovalSubmission submission, CancellationToken cancellationToken)
    {
        var existing = await GetFileAsync(path, branch, cancellationToken);
        if (existing is not null && existing.Content == content)
        {
            return false;
        }

        using var response = await _httpClient.PutAsJsonAsync(
            $"https://api.github.com/repos/{_repository}/contents/{path}",
            new
            {
                message = $"Approve {submission.ApplicationName} upgrade script for {submission.PlatformBucket}",
                content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
                branch,
                sha = existing?.Sha,
            },
            cancellationToken);
        await EnsureSuccessAsync(response, $"write {path}", cancellationToken);
        return true;
    }

    private async Task<string?> OpenPullRequestAsync(
        string branch, string baseBranch, ScriptApprovalSubmission submission, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            $"https://api.github.com/repos/{_repository}/pulls",
            new
            {
                title = $"Approve {submission.ApplicationName} upgrade script ({submission.PlatformBucket})",
                head = branch,
                @base = baseBranch,
                body = BuildPullRequestBody(submission),
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
    private static string BuildPullRequestBody(ScriptApprovalSubmission submission)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"`{submission.ApplicationName}` on `{submission.PlatformBucket}` "
            + $"({submission.Language.Interpreter()}), signed and reviewed on one Kintsugi server.");
        builder.AppendLine();
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
