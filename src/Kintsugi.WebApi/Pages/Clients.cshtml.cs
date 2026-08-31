using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Kintsugi.Application.AgentPackages;
using Kintsugi.Application.AgentPackages.Commands.ImportAgentPackagesFromSource;
using Kintsugi.Application.AgentPackages.Queries.GetAgentPackages;
using Kintsugi.Application.AgentPackages.Queries.GetAgentPackageSourceStatus;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.WebApi.Pages;

public class ClientsModel : PageModel
{
    private readonly ISender _sender;
    private readonly IAgentApiOptions _agentApiOptions;

    public ClientsModel(ISender sender, IAgentApiOptions agentApiOptions)
    {
        _sender = sender;
        _agentApiOptions = agentApiOptions;
    }

    public IReadOnlyList<AgentPackageDto> Packages { get; private set; } = Array.Empty<AgentPackageDto>();

    /// <summary>What the upstream repository currently offers — checked on every page load, which
    /// is the "check for new versions when clicked on" half of this page. Never null after a
    /// request: an unreachable upstream comes back with
    /// <see cref="AgentPackageSourceStatusDto.UnavailableReason"/> set rather than throwing.</summary>
    public AgentPackageSourceStatusDto SourceStatus { get; private set; } =
        new(string.Empty, Array.Empty<AgentPackageSourceStatusRow>(), UnavailableReason: null);

    /// <summary>Per-platform outcomes of the refresh that produced this render, or empty on a
    /// plain GET.</summary>
    public IReadOnlyList<AgentPackageImportResultDto> ImportResults { get; private set; } =
        Array.Empty<AgentPackageImportResultDto>();

    public string? RefreshError { get; private set; }

    /// <summary>
    /// The address baked into each imported package's bundled <c>config.toml</c>, and shown on the
    /// page so a wrong one is visible here rather than only as an agent that never checks in.
    ///
    /// <c>AGENT_API_BASE_URL</c> when it is set, and otherwise the address this page was reached
    /// on. The configured value has to win, because the browser's address is regularly *not* an
    /// address an agent can authenticate against: nginx is what verifies the client certificate,
    /// so anything terminating TLS in front of it — a gateway, a load balancer, a CDN — ends the
    /// mutual-TLS handshake at itself and cannot pass the certificate on. An earlier version of
    /// this page derived the address unconditionally and argued it was safe because nginx's
    /// plain-HTTP listener only ever 301s to the TLS one; that reasoning covers the scheme and the
    /// port and misses the front door entirely, and it shipped agents pointed at a gateway that
    /// enrolled fine and then 403'd on every authenticated route.
    /// </summary>
    public string AgentApiBaseUrl => _agentApiOptions.AgentApiBaseUrl ?? RequestBaseUrl;

    /// <summary>True when no <c>AGENT_API_BASE_URL</c> is configured and the address above is a
    /// guess from this request — which the page says out loud, because the guess being wrong is
    /// otherwise invisible until an agent has been installed and silently reports nothing.</summary>
    public bool AgentApiBaseUrlIsDerived => _agentApiOptions.AgentApiBaseUrl is null;

    /// <summary>The address this page was reached on — the forwarded pair nginx sends, so the
    /// browser's and not the container's (see <c>Program.cs</c>'s <c>UseForwardedHeaders</c>).</summary>
    public string RequestBaseUrl => $"{Request.Scheme}://{Request.Host}";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Downloads whatever the upstream repository has that this server doesn't, points it at this
    /// server, and publishes it locally.
    ///
    /// This is a page handler rather than a route on <c>AgentPackagesController</c> deliberately.
    /// nginx proxies <c>/api/agent-packages</c> on a prefix match with no client certificate
    /// required, and <c>Program.cs</c> exempts all of <c>/api</c> from the sign-in gate — so an
    /// API route here would be triggerable by anyone who can reach the server. A page handler sits
    /// behind that gate with the rest of the admin UI. Nothing in <c>nginx/default.conf</c> needs
    /// changing for this feature, which is the unusual case: for an agent-facing route it always
    /// would.
    /// </summary>
    public async Task<IActionResult> OnPostRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            ImportResults = await _sender.Send(new ImportAgentPackagesFromSourceCommand(AgentApiBaseUrl), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only reachable when listing the releases itself failed — a per-platform failure is
            // already reported as an outcome by the handler rather than thrown.
            RefreshError = ex.Message;
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Packages = await _sender.Send(new GetAgentPackagesQuery(), cancellationToken);
        SourceStatus = await _sender.Send(new GetAgentPackageSourceStatusQuery(), cancellationToken);
    }
}
