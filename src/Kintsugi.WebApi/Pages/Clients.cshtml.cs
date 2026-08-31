using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Kintsugi.Application.AgentPackages;
using Kintsugi.Application.AgentPackages.Commands.ImportAgentPackagesFromSource;
using Kintsugi.Application.AgentPackages.Queries.GetAgentPackages;
using Kintsugi.Application.AgentPackages.Queries.GetAgentPackageSourceStatus;

namespace Kintsugi.WebApi.Pages;

public class ClientsModel : PageModel
{
    private readonly ISender _sender;

    public ClientsModel(ISender sender)
    {
        _sender = sender;
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
    /// Built from the address this page was reached on — the forwarded pair nginx sends, so it is
    /// the browser's address and not the container's (see <c>Program.cs</c>'s
    /// <c>UseForwardedHeaders</c>, and the same reasoning behind the OIDC callback URLs on the
    /// Authentication settings page). That is a safe source for an address agents must reach over
    /// mutual TLS because nginx's plain-HTTP listener serves nothing but a 301 to the TLS one:
    /// there is no way to have reached this page over a scheme or port a client certificate
    /// couldn't be presented on.
    /// </summary>
    public string AgentApiBaseUrl => $"{Request.Scheme}://{Request.Host}";

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
