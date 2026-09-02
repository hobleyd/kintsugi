using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.AgentPackages;
using Kintsugi.Application.AgentPackages.Commands.ImportAgentPackagesFromSource;
using Kintsugi.Application.AgentPackages.Queries.GetAgentPackages;
using Kintsugi.Application.AgentPackages.Queries.GetAgentPackageSourceStatus;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The Clients screen: which agent packages are published here, what the upstream repository
/// currently offers, and the refresh that closes the gap.
/// </summary>
/// <remarks>
/// <para>
/// This replaces <c>Pages/Clients.cshtml.cs</c>, whose refresh was a page handler rather than an
/// API route <em>on purpose</em>: nginx proxies <c>/api/agent-packages</c> on a prefix match with
/// no client certificate required, and <c>Program.cs</c> exempts all of <c>/api</c> from the
/// sign-in gate, so an API route would have been triggerable by anyone who could reach the server.
/// That reasoning has not gone away — what has changed is that a page handler is no longer an
/// option, since the admin UI is now static files served by nginx and has no server-rendered page
/// to hang a handler off. <see cref="RequireAdminSessionAttribute"/> on the class is what replaces
/// the sign-in gate a page handler used to sit behind; without it this would be exactly the
/// anonymous route the old comment warned against.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/clients")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminClientsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly IAgentApiOptions _agentApiOptions;

    public AdminClientsController(ISender sender, IAgentApiOptions agentApiOptions)
    {
        _sender = sender;
        _agentApiOptions = agentApiOptions;
    }

    [HttpGet]
    [ProducesResponseType(typeof(ClientsViewDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ClientsViewDto>> Get(CancellationToken cancellationToken) =>
        Ok(await LoadAsync(Array.Empty<AgentPackageImportResultDto>(), refreshError: null, cancellationToken));

    /// <summary>
    /// Downloads whatever the upstream repository has that this server does not, points it at this
    /// server, and publishes it locally.
    /// </summary>
    /// <remarks>
    /// Returns the whole screen's state rather than just the outcome of the import, because the
    /// import changes what is published and what "newer available" means — the same reason the page
    /// handler this replaces re-ran its load before rendering. One response, one new state for the
    /// client to render, and no window in which the results are shown beside stale packages.
    /// </remarks>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(ClientsViewDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ClientsViewDto>> Refresh(CancellationToken cancellationToken)
    {
        var results = Array.Empty<AgentPackageImportResultDto>() as IReadOnlyList<AgentPackageImportResultDto>;
        string? refreshError = null;

        try
        {
            results = await _sender.Send(new ImportAgentPackagesFromSourceCommand(ResolveAgentApiBaseUrl()), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reported in the response body rather than thrown, matching the page handler: only
            // listing the releases can fail this way, and a per-platform failure already comes back
            // as an outcome. A refresh imports what it can, so "two of three worked" has to read as
            // two successes and one failure rather than as a failed request.
            refreshError = ex.Message;
        }

        return Ok(await LoadAsync(results, refreshError, cancellationToken));
    }

    private async Task<ClientsViewDto> LoadAsync(
        IReadOnlyList<AgentPackageImportResultDto> importResults,
        string? refreshError,
        CancellationToken cancellationToken) =>
        new(
            await _sender.Send(new GetAgentPackagesQuery(), cancellationToken),
            await _sender.Send(new GetAgentPackageSourceStatusQuery(), cancellationToken),
            ResolveAgentApiBaseUrl(),
            _agentApiOptions.AgentApiBaseUrl is null,
            RequestBaseUrl,
            importResults,
            refreshError);

    /// <summary>
    /// The address baked into each imported package's bundled <c>config.toml</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolved server-side, and the configured value always wins. The browser's address is
    /// regularly <em>not</em> an address an agent can authenticate against: nginx is what verifies
    /// the client certificate, so anything terminating TLS in front of it — a gateway, a load
    /// balancer, a CDN — ends the mutual-TLS handshake at itself and cannot pass the certificate
    /// on. An earlier version of the Clients page derived the address unconditionally and argued it
    /// was safe because nginx's plain-HTTP listener only ever 301s to the TLS one; that covers the
    /// scheme and the port and misses the front door, and it shipped agents pointed at a gateway
    /// that enrolled fine and then 403'd on every authenticated route.
    /// </para>
    /// <para>
    /// The fallback stays computed here, from this request, rather than being passed in by the
    /// client — a client-supplied base URL is a client-supplied instruction about what to bake into
    /// signed packages. It is the forwarded pair nginx sends, which it sets identically for an XHR
    /// and a page navigation, so this is the same value the page used to render.
    /// </para>
    /// </remarks>
    private string ResolveAgentApiBaseUrl() => _agentApiOptions.AgentApiBaseUrl ?? RequestBaseUrl;

    private string RequestBaseUrl => $"{Request.Scheme}://{Request.Host}";
}

/// <param name="Packages">Agent packages published on this server, one row per platform.</param>
/// <param name="SourceStatus">What the upstream repository currently offers, checked on every read —
/// the "check for new versions" half of this screen. Never null: an unreachable upstream comes back
/// with <see cref="AgentPackageSourceStatusDto.UnavailableReason"/> set rather than throwing, because
/// the packages already published here are installable whether or not GitHub is reachable.</param>
/// <param name="AgentApiBaseUrl">The address baked into each imported package's bundled
/// <c>config.toml</c>.</param>
/// <param name="AgentApiBaseUrlIsDerived">True when no <c>AGENT_API_BASE_URL</c> is configured and
/// the address above is a guess from this request — which the client says out loud, because the
/// guess being wrong is otherwise invisible until an agent has been installed and silently reports
/// nothing.</param>
/// <param name="RequestBaseUrl">The address this request arrived on, so the client can name it in
/// that warning rather than only showing the resolved value.</param>
/// <param name="ImportResults">Per-platform outcomes of the refresh that produced this response,
/// or empty for a plain read.</param>
/// <param name="RefreshError">Set only when listing the upstream releases failed outright. A
/// per-platform failure is an entry in <paramref name="ImportResults"/> instead.</param>
public record ClientsViewDto(
    IReadOnlyList<AgentPackageDto> Packages,
    AgentPackageSourceStatusDto SourceStatus,
    string AgentApiBaseUrl,
    bool AgentApiBaseUrlIsDerived,
    string RequestBaseUrl,
    IReadOnlyList<AgentPackageImportResultDto> ImportResults,
    string? RefreshError);
