using System.Security.Cryptography;
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
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISender _sender;

    public ImportAgentPackagesFromSourceCommandHandler(
        IAgentPackageSourceClient sourceClient,
        IAgentPackageArchiveRewriter archiveRewriter,
        IAgentPackageRepository repository,
        IUnitOfWork unitOfWork,
        ISender sender)
    {
        _sourceClient = sourceClient;
        _archiveRewriter = archiveRewriter;
        _repository = repository;
        _unitOfWork = unitOfWork;
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
            // A Windows row published before the upstream pin was recorded has nothing for
            // WindowsBootstrapScript to pin, and would otherwise stay that way until the next agent
            // release — which on a settled fleet could be weeks. One download backfills it, and
            // because RecordUpstreamProvenance only ever fills a gap, this costs nothing on every
            // subsequent refresh. Failure is swallowed: the archive already published here is
            // installable either way, and turning an up-to-date refresh into a reported error over
            // a missing convenience would be a worse trade.
            await TryBackfillUpstreamProvenanceAsync(existing, release, cancellationToken);

            return new AgentPackageImportResultDto(
                release.Platform, release.Version, AgentPackageImportOutcome.AlreadyPublished, Message: null);
        }

        try
        {
            await using var downloaded = await _sourceClient.DownloadAsync(release, cancellationToken);

            // Hashed here, over what GitHub actually served, and before the rewrite below touches
            // it. This is the pin the Windows bootstrap script carries — that script fetches from
            // GitHub, not from this server, so the stored archive's own checksum (taken after the
            // rewrite, and the one the agent's self-update verifies) says nothing about what the
            // script will receive.
            var upstreamSha256 = await ComputeSha256Async(downloaded, cancellationToken);

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
                    configured,
                    upstreamSha256,
                    release.DownloadUrl),
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

    /// <summary>The one platform whose pin is actually consumed — <c>WindowsBootstrapScript</c>
    /// is Windows-only, because CrowdStrike deployment is. The provenance column itself is not
    /// platform-specific and every import records it for free, since the bytes are already in
    /// hand; it is only this backfill that costs a download, and spending three of them on a
    /// routine refresh so that two platforms can carry a pin nothing reads would be waste on
    /// every press of a button somebody presses often.</summary>
    private const string BackfillPlatform = "windows";

    private async Task TryBackfillUpstreamProvenanceAsync(
        Domain.Entities.AgentPackage existing,
        AgentPackageSourceRelease release,
        CancellationToken cancellationToken)
    {
        if (existing.UpstreamSha256 is not null
            || !string.Equals(existing.Platform, BackfillPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await using var downloaded = await _sourceClient.DownloadAsync(release, cancellationToken);
            if (existing.RecordUpstreamProvenance(
                    await ComputeSha256Async(downloaded, cancellationToken), release.DownloadUrl))
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately silent. See the call site: this is a convenience for a screen, not part
            // of publishing anything, and the next refresh will try again.
            _ = ex;
        }
    }

    /// <summary>Hashes a stream and rewinds it, so the caller can go on to read the same bytes.
    /// The source client hands back a seekable MemoryStream for exactly this kind of second
    /// reading — see its own note on why it buffers.</summary>
    private static async Task<string> ComputeSha256Async(Stream content, CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();
        await using (var hashingStream = new CryptoStream(Stream.Null, sha256, CryptoStreamMode.Write))
        {
            await content.CopyToAsync(hashingStream, cancellationToken);
        }

        content.Position = 0;
        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }

    /// <summary>Matches PublishAgentPackageCommandValidator's own cap on release notes — a GitHub
    /// release body has no length limit worth relying on, and losing the tail of a description is
    /// a far better outcome than failing the import on a validation error.</summary>
    private const int MaxReleaseNotesLength = 2000;

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
