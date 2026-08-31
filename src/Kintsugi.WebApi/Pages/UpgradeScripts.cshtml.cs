using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Kintsugi.Application.ScriptApproval.Commands.AdoptApprovedScript;
using Kintsugi.Application.ScriptApproval.Commands.ImportApprovedScriptsFromSource;
using Kintsugi.Application.ScriptApproval.Queries.GetApprovedScripts;

namespace Kintsugi.WebApi.Pages;

public class UpgradeScriptsModel : PageModel
{
    private readonly ISender _sender;

    public UpgradeScriptsModel(ISender sender)
    {
        _sender = sender;
    }

    public UpgradeScriptsOverviewDto Overview { get; private set; } = default!;

    /// <summary>What the refresh that produced this render did, or null on a plain GET.</summary>
    public ImportApprovedScriptsResultDto? ImportResult { get; private set; }

    public string? RefreshError { get; private set; }

    public string? AdoptError { get; private set; }

    public AdoptApprovedScriptResultDto? Adopted { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Brings this server up to date with the approval repository's default branch.
    ///
    /// A page handler rather than a route on a controller, for the reason
    /// <c>ClientsModel.OnPostRefreshAsync</c> spells out and which applies with more force here:
    /// <c>Program.cs</c> exempts all of <c>/api</c> from the sign-in gate, so an API route would be
    /// triggerable by anyone who can reach the server — and this one changes what agents execute. A
    /// page handler sits behind that gate with the rest of the admin UI.
    /// </summary>
    public async Task<IActionResult> OnPostRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            ImportResult = await _sender.Send(new ImportApprovedScriptsFromSourceCommand(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only reachable when the repository itself couldn't be read — a malformed individual
            // entry is reported as a rejection by the handler rather than thrown.
            RefreshError = ex.Message;
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    /// <summary>
    /// Takes one approved script onto one local upgrade path. Deliberately per-row and human-pressed
    /// — see <c>AdoptApprovedScriptCommand</c> for why this is not something the refresh does by
    /// itself.
    /// </summary>
    public async Task<IActionResult> OnPostAdoptAsync(
        string applicationName, string platform, string sha256, string signerFingerprint, CancellationToken cancellationToken)
    {
        try
        {
            Adopted = await _sender.Send(
                new AdoptApprovedScriptCommand(applicationName, platform, sha256, signerFingerprint), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AdoptError = ex.Message;
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Overview = await _sender.Send(new GetUpgradeScriptsOverviewQuery(), cancellationToken);
    }
}
