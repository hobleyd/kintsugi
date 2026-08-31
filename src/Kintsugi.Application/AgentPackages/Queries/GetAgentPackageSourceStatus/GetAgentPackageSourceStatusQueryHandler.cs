using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AgentPackages.Queries.GetAgentPackageSourceStatus;

public class GetAgentPackageSourceStatusQueryHandler
    : IRequestHandler<GetAgentPackageSourceStatusQuery, AgentPackageSourceStatusDto>
{
    private readonly IAgentPackageSourceClient _sourceClient;
    private readonly IAgentPackageRepository _repository;

    public GetAgentPackageSourceStatusQueryHandler(
        IAgentPackageSourceClient sourceClient,
        IAgentPackageRepository repository)
    {
        _sourceClient = sourceClient;
        _repository = repository;
    }

    public async Task<AgentPackageSourceStatusDto> Handle(
        GetAgentPackageSourceStatusQuery request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentPackageSourceRelease> releases;
        try
        {
            releases = await _sourceClient.GetLatestReleasesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This query runs on every Clients page load, and the packages already published here
            // are installable whether or not GitHub is reachable — so an unreachable upstream is
            // reported on the page and nothing more. Throwing would take the downloads down with
            // it, which is exactly backwards. The reason travels as data rather than to a log
            // nobody reads: it is shown on the page beside the still-working downloads.
            return new AgentPackageSourceStatusDto(_sourceClient.SourceDescription, Array.Empty<AgentPackageSourceStatusRow>(), ex.Message);
        }

        var published = await _repository.GetLatestPerPlatformAsync(cancellationToken);
        var publishedByPlatform = published.ToDictionary(p => p.Platform, p => p.Version, StringComparer.OrdinalIgnoreCase);

        var rows = releases
            .Select(release =>
            {
                publishedByPlatform.TryGetValue(release.Platform, out var publishedVersion);
                return new AgentPackageSourceStatusRow(
                    release.Platform,
                    release.Version,
                    publishedVersion,
                    AgentPackageVersion.IsNewer(release.Version, publishedVersion));
            })
            .OrderBy(row => row.Platform, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AgentPackageSourceStatusDto(_sourceClient.SourceDescription, rows, UnavailableReason: null);
    }
}
