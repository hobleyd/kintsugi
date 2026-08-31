using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Kintsugi.Application.AgentPackages;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.AgentPackages;

/// <summary>
/// Reads client builds out of the project's own public GitHub repository's Releases — one release
/// per agent per version, tagged <c>&lt;platform&gt;-agent-v&lt;version&gt;</c> and carrying a
/// single <c>.tar.gz</c> asset, exactly as <c>.github/workflows/ci.yml</c> publishes them.
/// </summary>
public class GitHubAgentPackageSourceClient : IAgentPackageSourceClient
{
    /// <summary>Which repository to pull builds from. Overridable so a fork, or an internal mirror
    /// of this project, can be the upstream instead — the default is this project's own public
    /// repository, which is already named in CLAUDE.md and is not deployment detail.</summary>
    public const string RepositoryConfigurationKey = "AGENT_PACKAGE_GITHUB_REPO";
    private const string DefaultRepository = "hobleyd/kintsugi";

    /// <summary>The tag shape <c>ci.yml</c> creates. Both halves are load-bearing: the platform
    /// is the agent-package namespace ("macos"/"windows"/"linux" — not <c>PlatformBucket</c>'s),
    /// and the version is what a published <c>AgentPackage</c> is keyed by, so renaming a tag on
    /// either side silently stops that platform ever being found again.</summary>
    private static readonly Regex TagPattern = new(
        @"^(?<platform>macos|windows|linux)-agent-v(?<version>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Listing releases runs on every Clients page load, so it gets a far shorter leash
    /// than the download does — a slow or unreachable GitHub must not hold the page open for the
    /// HttpClient's default hundred seconds. The failure is reported on the page, not thrown; see
    /// <c>GetAgentPackageSourceStatusQueryHandler</c>.</summary>
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly string _repository;

    public GitHubAgentPackageSourceClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;

        var configured = configuration[RepositoryConfigurationKey];
        _repository = string.IsNullOrWhiteSpace(configured) ? DefaultRepository : configured.Trim().Trim('/');

        // GitHub rejects an API request with no User-Agent outright, with a 403 that says nothing
        // about the real cause.
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Kintsugi-Server");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        // The same optional token the upgrade-path research path uses (see
        // AiUpgradePathResearchClient) — no scopes needed for a public repository, only a higher
        // rate limit than the 60 requests/hour an unauthenticated caller gets.
        var token = configuration["GITHUB_API_TOKEN"];
        if (!string.IsNullOrWhiteSpace(token))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }
    }

    public string SourceDescription => _repository;

    public async Task<IReadOnlyList<AgentPackageSourceRelease>> GetLatestReleasesAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MetadataTimeout);

        string json;
        try
        {
            using var response = await _httpClient.GetAsync(
                $"https://api.github.com/repos/{_repository}/releases?per_page=100", timeout.Token);
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller didn't cancel, so this is MetadataTimeout firing. Surfaced as a timeout
            // rather than a cancellation because the caller treats cancellation as "the request
            // went away" and lets it propagate, which would take the page down with it.
            throw new TimeoutException(
                $"Listing releases from {_repository} took longer than {MetadataTimeout.TotalSeconds:0} seconds.");
        }

        return ParseLatestReleases(json);
    }

    public async Task<Stream> DownloadAsync(AgentPackageSourceRelease release, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(release.DownloadUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Buffered rather than handed back as the live response stream: the archive rewriter reads
        // it twice over (decompress, then re-encode) and PublishAgentPackageCommandHandler needs
        // to seek it, neither of which a network stream supports. An installer bundle is a few MB.
        var buffered = new MemoryStream();
        await response.Content.CopyToAsync(buffered, cancellationToken);
        buffered.Position = 0;
        return buffered;
    }

    /// <summary>
    /// Picks the newest release for each platform out of a GitHub releases listing. Pulled out as
    /// a pure function over the raw JSON so it can be tested against a captured real payload,
    /// rather than only against whatever GitHub happens to be serving — the same reason each
    /// agent's package-manager output parsers take a plain string.
    ///
    /// Drafts are skipped (nothing has actually been published yet); pre-releases are not, so a
    /// deliberate pre-release tag is still installable. A release whose tag doesn't match
    /// <see cref="TagPattern"/>, or that carries no <c>.tar.gz</c> asset, is ignored rather than
    /// treated as an error — this repository is free to publish releases that have nothing to do
    /// with agent builds.
    /// </summary>
    public static IReadOnlyList<AgentPackageSourceRelease> ParseLatestReleases(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AgentPackageSourceRelease>();
        }

        var newestPerPlatform = new Dictionary<string, AgentPackageSourceRelease>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var release = ToRelease(element);
            if (release is null)
            {
                continue;
            }

            // GitHub returns newest-created first, but "created most recently" and "highest
            // version" are not the same thing once a patch is backported or a release is re-cut,
            // and the version is what everything downstream keys on.
            if (!newestPerPlatform.TryGetValue(release.Platform, out var incumbent)
                || AgentPackageVersion.IsNewer(release.Version, incumbent.Version))
            {
                newestPerPlatform[release.Platform] = release;
            }
        }

        return newestPerPlatform.Values
            .OrderBy(r => r.Platform, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AgentPackageSourceRelease? ToRelease(JsonElement element)
    {
        if (element.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        if (!element.TryGetProperty("tag_name", out var tagName) || tagName.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var match = TagPattern.Match(tagName.GetString()!);
        if (!match.Success)
        {
            return null;
        }

        if (!element.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || !asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var fileName = name.GetString()!;
            if (!fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new AgentPackageSourceRelease(
                match.Groups["platform"].Value,
                match.Groups["version"].Value,
                fileName,
                url.GetString()!,
                element.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
                    ? NullIfBlank(body.GetString())
                    : null);
        }

        return null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
