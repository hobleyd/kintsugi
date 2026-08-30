using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.AgentPackages;
using Kintsugi.Application.AgentPackages.Commands.PublishAgentPackage;
using Kintsugi.Application.AgentPackages.Queries.GetAgentPackages;
using Kintsugi.Application.AgentPackages.Queries.GetLatestAgentPackage;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// Installable kintsugi-agent builds. Deliberately not gated behind
/// <see cref="Filters.RequireAgentIdentityAttribute"/> like host/application registration is — the
/// listing and download routes need to work from a plain browser (the Clients page) and from an
/// agent that hasn't necessarily enrolled yet, and the package content itself is authenticated by
/// its own signature (see <see cref="AgentPackageDto.Sha256Signature"/>), not by who's asking for it.
/// </summary>
[ApiController]
[Route("api/agent-packages")]
[Produces("application/json")]
public class AgentPackagesController : ControllerBase
{
    /// <summary>Set by nginx to the TLS-layer client-certificate verification result for this
    /// request — "SUCCESS" when a certificate signed by the agent fleet CA was presented and
    /// verified, "NONE" when none was presented at all (see nginx/default.conf's
    /// <c>/api/agent-packages</c> block). Unlike <see cref="Filters.RequireAgentIdentityAttribute"/>'s
    /// header, this route never rejects a request for lacking one — it only changes how
    /// <see cref="Download"/> behaves once one is present.</summary>
    public const string AgentCertVerifiedHeader = "X-Agent-Cert-Verified";

    private readonly ISender _sender;
    private readonly IAgentPackageStorage _storage;
    private readonly IAgentPackageArchiveRewriter _archiveRewriter;
    private readonly IAgentEnrollmentOptions _enrollmentOptions;

    public AgentPackagesController(
        ISender sender,
        IAgentPackageStorage storage,
        IAgentPackageArchiveRewriter archiveRewriter,
        IAgentEnrollmentOptions enrollmentOptions)
    {
        _sender = sender;
        _storage = storage;
        _archiveRewriter = archiveRewriter;
        _enrollmentOptions = enrollmentOptions;
    }

    /// <summary>Lists the latest published package for every platform — what the Clients page
    /// offers for download.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AgentPackageDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AgentPackageDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetAgentPackagesQuery(), cancellationToken));

    /// <summary>
    /// The latest published package for one platform — polled by the kintsugi-agent itself (see
    /// clients/macos-agent/src/self_update.rs) at every check-in to decide whether it needs to
    /// update itself.
    /// </summary>
    [HttpGet("{platform}/latest")]
    [ProducesResponseType(typeof(AgentPackageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentPackageDto>> GetLatest(string platform, CancellationToken cancellationToken)
    {
        var package = await _sender.Send(new GetLatestAgentPackageQuery(platform), cancellationToken);
        return package is null ? NotFound() : Ok(package);
    }

    /// <summary>
    /// Downloads the latest published package file for one platform. For an anonymous request
    /// (a browser on the Clients page, doing a fresh install), the current
    /// <c>AGENT_ENROLLMENT_TOKEN</c> is substituted into the archive's <c>config.toml</c> entry —
    /// see <see cref="IAgentPackageArchiveRewriter"/> for why that happens on every download
    /// rather than once at publish time. An already-enrolled agent's own self-update check (see
    /// clients/macos-agent/src/self_update.rs) authenticates with its own client certificate and
    /// gets the archive back byte-for-byte as published instead — it already has a working
    /// identity and doesn't need a token rewritten in, and rewriting it would change the archive's
    /// bytes enough that its checksum would no longer match the one signed at publish time.
    /// </summary>
    [HttpGet("{platform}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(string platform, CancellationToken cancellationToken)
    {
        var package = await _sender.Send(new GetLatestAgentPackageQuery(platform), cancellationToken);
        if (package is null)
        {
            return NotFound();
        }

        var storedStream = _storage.OpenRead(package.Platform, package.FileName);

        if (RequestPresentedAVerifiedAgentCertificate(Request.Headers[AgentCertVerifiedHeader]))
        {
            return File(storedStream, "application/gzip", package.FileName);
        }

        try
        {
            var rewritten = await _archiveRewriter.WithEnrollmentToken(storedStream, _enrollmentOptions.EnrollmentToken, cancellationToken);
            return File(rewritten, "application/gzip", package.FileName);
        }
        finally
        {
            await storedStream.DisposeAsync();
        }
    }

    /// <summary>Pulled out as a pure function purely so the header-value contract (exactly
    /// "SUCCESS", set by nginx's <c>$ssl_client_verify</c> — never "FAILED:..." or "NONE") is
    /// unit-testable without standing up a full HTTP request.</summary>
    public static bool RequestPresentedAVerifiedAgentCertificate(string? headerValue) =>
        string.Equals(headerValue, "SUCCESS", StringComparison.Ordinal);

    /// <summary>
    /// Publishes a new build for a platform — meant to be called from a release script (see
    /// clients/macos-agent/packaging/publish-release.sh) or a CI job, not from the browser UI.
    /// Idempotent: re-publishing the exact same (platform, version, content) that's already
    /// published is a no-op that returns the existing record, so a pipeline can call this on
    /// every build without checking first. Publishing different content under a (platform,
    /// version) pair that's already out is still rejected — there is no overwrite/replace.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(200_000_000)]
    [ProducesResponseType(typeof(AgentPackageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AgentPackageDto>> Publish(
        [FromForm] string platform,
        [FromForm] string version,
        [FromForm] string? releaseNotes,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        var command = new PublishAgentPackageCommand(platform, version, releaseNotes, file.FileName, stream);
        return Ok(await _sender.Send(command, cancellationToken));
    }
}
