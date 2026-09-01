using System.Text.Json;
using System.Text.RegularExpressions;
using Kintsugi.Application.AgentPackages;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Infrastructure.ScriptApproval;

namespace Kintsugi.Infrastructure.AgentPackages;

/// <summary>
/// Reads client builds out of the project's own public GitHub repository's Releases — one release
/// per agent per version, tagged <c>&lt;platform&gt;-agent-v&lt;version&gt;</c> and carrying a
/// single <c>.tar.gz</c> asset, exactly as <c>.github/workflows/ci.yml</c> publishes them.
/// </summary>
public class GitHubAgentPackageSourceClient : IAgentPackageSourceClient
{
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
    private readonly IGitHubSettingsProvider _settingsProvider;

    public GitHubAgentPackageSourceClient(HttpClient httpClient, IGitHubSettingsProvider settingsProvider)
    {
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
        ScriptApprovalGitHubHeaders.ApplyStaticHeaders(_httpClient);
    }

    public async Task<IReadOnlyList<AgentPackageSourceRelease>> GetLatestReleasesAsync(CancellationToken cancellationToken)
    {
        // Read per call, not captured in the constructor: both the repository and the token are
        // editable on the GitHub settings page now, so a captured value would ignore every edit
        // until the next restart. See GitHubSettings.
        var settings = await _settingsProvider.GetAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MetadataTimeout);

        string json;
        try
        {
            // The token here is the read-only one — no scopes needed against a public repository,
            // only a higher rate limit than the 60 requests/hour an anonymous caller gets. It is
            // deliberately not the script-approval token, which can write.
            using var request = ScriptApprovalGitHubHeaders.Request(
                HttpMethod.Get, $"https://api.github.com/repos/{settings.AgentPackageRepository}/releases?per_page=100",
                settings.ApiToken);
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller didn't cancel, so this is MetadataTimeout firing. Surfaced as a timeout
            // rather than a cancellation because the caller treats cancellation as "the request
            // went away" and lets it propagate, which would take the page down with it.
            throw new TimeoutException(
                $"Listing releases from {settings.AgentPackageRepository} took longer than {MetadataTimeout.TotalSeconds:0} seconds.");
        }

        return ParseLatestReleases(json);
    }

    public async Task<Stream> DownloadAsync(AgentPackageSourceRelease release, CancellationToken cancellationToken)
    {
        var settings = await _settingsProvider.GetAsync(cancellationToken);

        using var request = ScriptApprovalGitHubHeaders.Request(HttpMethod.Get, release.DownloadUrl, settings.ApiToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
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
            // and the version is what everything downstream keys on. IsHigherThan, not IsNewer:
            // only a provably higher version displaces the incumbent, so two versions that can't
            // be ordered against each other (a pre-release tag beside its final release) fall back
            // to GitHub's newest-created-first order instead of to whichever was listed second.
            if (!newestPerPlatform.TryGetValue(release.Platform, out var incumbent)
                || AgentPackageVersion.IsHigherThan(release.Version, incumbent.Version))
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
