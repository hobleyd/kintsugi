using MediatR;
using Kintsugi.Application.AgentPackages.Commands.PublishAgentPackage;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AgentPackages.Commands.ImportAgentPackagesFromSource;

public class ImportAgentPackagesFromSourceCommandHandler
    : IRequestHandler<ImportAgentPackagesFromSourceCommand, IReadOnlyList<AgentPackageImportResultDto>>
{
    private readonly IAgentPackageSourceClient _sourceClient;
    private readonly IAgentPackageArchiveRewriter _archiveRewriter;
    private readonly IAgentPackageRepository _repository;
    private readonly ISender _sender;

    public ImportAgentPackagesFromSourceCommandHandler(
        IAgentPackageSourceClient sourceClient,
        IAgentPackageArchiveRewriter archiveRewriter,
        IAgentPackageRepository repository,
        ISender sender)
    {
        _sourceClient = sourceClient;
        _archiveRewriter = archiveRewriter;
        _repository = repository;
        _sender = sender;
    }

    public async Task<IReadOnlyList<AgentPackageImportResultDto>> Handle(
        ImportAgentPackagesFromSourceCommand request,
        CancellationToken cancellationToken)
    {
        // One build per platform out of the whole listing: importing every intermediate version
        // would only publish archives no agent will ever download, since self_update reads the
        // latest package and nothing else.
        var releases = AgentPackageReleases.LatestPerPlatform(await _sourceClient.GetReleasesAsync(cancellationToken));
        var results = new List<AgentPackageImportResultDto>();

        foreach (var release in releases)
        {
            results.Add(await ImportOneAsync(release, request.ApiBaseUrl, cancellationToken));
        }

        return results;
    }

    private async Task<AgentPackageImportResultDto> ImportOneAsync(
        AgentPackageSourceRelease release,
        string apiBaseUrl,
        CancellationToken cancellationToken)
    {
        // Checked before downloading, not left to PublishAgentPackageCommandHandler's own
        // idempotency: that one compares bytes, and these bytes are only identical while
        // apiBaseUrl is unchanged. A server that moved address would otherwise fail the publish
        // with "already published with different content" — which is the right answer for a
        // release script that forgot to bump a version, and the wrong one here.
        var existing = await _repository.GetByPlatformAndVersionAsync(release.Platform, release.Version, cancellationToken);
        if (existing is not null)
        {
            return new AgentPackageImportResultDto(
                release.Platform, release.Version, AgentPackageImportOutcome.AlreadyPublished, Message: null);
        }

        try
        {
            await using var downloaded = await _sourceClient.DownloadAsync(release, cancellationToken);

            // The upstream archive ships the placeholder kintsugi.example.com, because a real
            // address must never be committed to a public repository. Rewriting it here rather
            // than on each download means the stored bytes — and the checksum signed over them —
            // already describe this server, so an enrolled agent's byte-identical self-update
            // download still verifies. See IAgentPackageArchiveRewriter.
            await using var configured = await _archiveRewriter.WithApiBaseUrl(downloaded, apiBaseUrl, cancellationToken);

            var published = await _sender.Send(
                new PublishAgentPackageCommand(
                    release.Platform,
                    release.Version,
                    Truncate(release.ReleaseNotes, MaxReleaseNotesLength),
                    release.FileName,
                    configured),
                cancellationToken);

            return new AgentPackageImportResultDto(
                published.Platform, published.Version, AgentPackageImportOutcome.Imported, Message: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One platform's failure must not cost the others their import — a fleet that got two
            // of three agents refreshed is strictly better off than one that got none, and the
            // page reports per-platform outcomes so the failure is still visible there.
            return new AgentPackageImportResultDto(
                release.Platform, release.Version, AgentPackageImportOutcome.Failed, ex.Message);
        }
    }

    /// <summary>Matches PublishAgentPackageCommandValidator's own cap on release notes — a GitHub
    /// release body has no length limit worth relying on, and losing the tail of a description is
    /// a far better outcome than failing the import on a validation error.</summary>
    private const int MaxReleaseNotesLength = 2000;

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
