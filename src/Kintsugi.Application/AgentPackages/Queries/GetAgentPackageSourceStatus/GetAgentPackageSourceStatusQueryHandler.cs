using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AgentPackages.Queries.GetAgentPackageSourceStatus;

public class GetAgentPackageSourceStatusQueryHandler
    : IRequestHandler<GetAgentPackageSourceStatusQuery, AgentPackageSourceStatusDto>
{
    private readonly IAgentPackageSourceClient _sourceClient;
    private readonly IAgentPackageRepository _repository;
    private readonly IGitHubSettingsProvider _gitHubSettings;

    public GetAgentPackageSourceStatusQueryHandler(
        IAgentPackageSourceClient sourceClient,
        IAgentPackageRepository repository,
        IGitHubSettingsProvider gitHubSettings)
    {
        _sourceClient = sourceClient;
        _repository = repository;
        _gitHubSettings = gitHubSettings;
    }

    public async Task<AgentPackageSourceStatusDto> Handle(
        GetAgentPackageSourceStatusQuery request,
        CancellationToken cancellationToken)
    {
        // Which repository builds come from is configuration, so it is read here rather than asked
        // of the client — the client no longer exposes it, precisely because a settings-page value
        // must not be captured anywhere.
        var settings = await _gitHubSettings.GetAsync(cancellationToken);

        IReadOnlyList<AgentPackageSourceRelease> releases;
        try
        {
            releases = await _sourceClient.GetReleasesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This query runs on every Clients page load, and the packages already published here
            // are installable whether or not GitHub is reachable — so an unreachable upstream is
            // reported on the page and nothing more. Throwing would take the downloads down with
            // it, which is exactly backwards. The reason travels as data rather than to a log
            // nobody reads: it is shown on the page beside the still-working downloads.
            return new AgentPackageSourceStatusDto(settings.AgentPackageRepository, Array.Empty<AgentPackageSourceStatusRow>(), ex.Message);
        }

        var published = await _repository.GetLatestPerPlatformAsync(cancellationToken);
        var publishedByPlatform = published.ToDictionary(p => p.Platform, p => p.Version, StringComparer.OrdinalIgnoreCase);

        var rows = AgentPackageReleases.LatestPerPlatform(releases)
            .Select(release =>
            {
                publishedByPlatform.TryGetValue(release.Platform, out var publishedVersion);
                return new AgentPackageSourceStatusRow(
                    release.Platform,
                    release.Version,
                    publishedVersion,
                    AgentPackageVersion.IsNewer(release.Version, publishedVersion),
                    AgentPackageReleases.NewerThan(releases, release.Platform, publishedVersion)
                        .Select(r => new AgentPackageReleaseNotesDto(r.Version, r.ReleaseNotes))
                        .ToList());
            })
            .ToList();

        return new AgentPackageSourceStatusDto(settings.AgentPackageRepository, rows, UnavailableReason: null);
    }
}
