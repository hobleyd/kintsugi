using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Kintsugi.Application.ScriptApproval;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Infrastructure.ScriptApproval;

namespace Kintsugi.Tests.Infrastructure;

/// <summary>
/// Covers <see cref="GitHubScriptApprovalPublisher"/> against a fake GitHub. The property under test
/// is the one the Upgrade Scripts flow depends on: signing bytes that are already on the trust root
/// proposes nothing, whoever signed them there — a second server signing the Homebrew script it took
/// from a newer build must not open a pull request rewriting one signature file, which is what the
/// old signature-document comparison did on every re-sign (ECDSA signatures are randomised per
/// signing, so the document never matched).
/// </summary>
public class GitHubScriptApprovalPublisherTests
{
    private const string Repository = "example/approved-scripts";
    // The builder's own text, so ApprovedScriptIdentity recognises it as the shared Homebrew script
    // and files it as `homebrew.sh` — the case the publisher's first check exists for.
    private static readonly string Script = HomebrewUpgradeScript.Build();
    private const string LocalFingerprint = "sha256:" + "aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111";
    private const string OtherFingerprint = "sha256:" + "bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222";

    private static readonly string Sha256 = ScriptContentHash.Of(Script);

    private static ScriptApprovalSubmission Submission() => new(
        Sha256,
        PlatformBucket.ForPackageManager(PackageManagerCatalog.Homebrew),
        ScriptLanguage.Bash,
        Script,
        "ada-url",
        "ada-url",
        LocalFingerprint,
        "-----BEGIN PUBLIC KEY-----\nMFkw\n-----END PUBLIC KEY-----\n",
        "MEUCIQDsignature",
        "admin@example.com",
        new DateTimeOffset(2026, 9, 7, 1, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// Enough of GitHub's contents/refs/pulls API to drive one publish: files on <c>main</c> are
    /// whatever the test seeded, every other read is a 404, and every write is recorded and accepted.
    /// </summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public Dictionary<string, string> FilesOnMain { get; } = new(StringComparer.Ordinal);
        public List<HttpRequestMessage> Writes { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;

            if (request.Method != HttpMethod.Get)
            {
                Writes.Add(request);
                _ = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json(HttpStatusCode.Created, new { html_url = $"https://github.com/{Repository}/pull/1" });
            }

            if (path == $"/repos/{Repository}")
            {
                return Json(HttpStatusCode.OK, new { default_branch = "main" });
            }

            if (path.StartsWith($"/repos/{Repository}/contents/", StringComparison.Ordinal))
            {
                var file = Uri.UnescapeDataString(path[$"/repos/{Repository}/contents/".Length..]);
                if (query == "?ref=main" && FilesOnMain.TryGetValue(file, out var content))
                {
                    return Json(HttpStatusCode.OK, new
                    {
                        sha = "blob-" + file.GetHashCode(),
                        content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
                    });
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path == $"/repos/{Repository}/pulls")
            {
                return Json(HttpStatusCode.OK, Array.Empty<object>());
            }

            if (path == $"/repos/{Repository}/git/ref/heads/main")
            {
                return Json(HttpStatusCode.OK, new { @object = new { sha = "abc123" } });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    private static GitHubScriptApprovalPublisher Publisher(FakeGitHub gitHub) => new(
        new HttpClient(gitHub),
        FakeGitHubSettings.Provider(scriptApprovalRepository: Repository, scriptApprovalToken: "ghp_test"),
        NullLogger<GitHubScriptApprovalPublisher>.Instance);

    [Fact]
    public async Task PublishAsync_EntryAlreadyOnDefaultBranchUnderAnotherSigner_ProposesNothing()
    {
        var gitHub = new FakeGitHub();
        gitHub.FilesOnMain[ApprovedScriptCorpus.MetadataPath(Sha256)] = "{}";
        gitHub.FilesOnMain[ApprovedScriptCorpus.SignaturePath(Sha256, OtherFingerprint)] = "{}";

        var result = await Publisher(gitHub).PublishAsync(Submission(), CancellationToken.None);

        Assert.Equal(ScriptApprovalPublishOutcome.AlreadyApproved, result.Outcome);
        Assert.Null(result.PullRequestUrl);
        Assert.Empty(gitHub.Writes);
    }

    [Fact]
    public async Task PublishAsync_EntryAlreadyOnDefaultBranchUnderThisSigner_ProposesNothingEvenThoughTheSignatureDiffers()
    {
        var gitHub = new FakeGitHub();
        gitHub.FilesOnMain[ApprovedScriptCorpus.MetadataPath(Sha256)] = "{}";
        // A previous signing by this same server: different signature bytes and a different
        // timestamp, as every ECDSA re-sign produces.
        gitHub.FilesOnMain[ApprovedScriptCorpus.SignaturePath(Sha256, LocalFingerprint)] = "{\"signature\":\"older\"}";

        var result = await Publisher(gitHub).PublishAsync(Submission(), CancellationToken.None);

        Assert.Equal(ScriptApprovalPublishOutcome.AlreadyApproved, result.Outcome);
        Assert.Empty(gitHub.Writes);
    }

    [Fact]
    public async Task PublishAsync_NewContent_WritesTheEntryUnderItsDescriptiveNameAndOpensAPullRequest()
    {
        var gitHub = new FakeGitHub();

        var result = await Publisher(gitHub).PublishAsync(Submission(), CancellationToken.None);

        Assert.Equal(ScriptApprovalPublishOutcome.PullRequestOpened, result.Outcome);
        Assert.Equal($"https://github.com/{Repository}/pull/1", result.PullRequestUrl);

        var written = gitHub.Writes
            .Where(request => request.Method == HttpMethod.Put)
            .Select(request => Uri.UnescapeDataString(request.RequestUri!.AbsolutePath[$"/repos/{Repository}/contents/".Length..]))
            .ToList();
        Assert.Equal(
            new[]
            {
                ApprovedScriptCorpus.ScriptPath(Sha256, "homebrew", ScriptLanguage.Bash),
                ApprovedScriptCorpus.MetadataPath(Sha256),
                ApprovedScriptCorpus.SignaturePath(Sha256, LocalFingerprint),
            },
            written);
        Assert.Contains(gitHub.Writes, request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == $"/repos/{Repository}/pulls");
    }
}
