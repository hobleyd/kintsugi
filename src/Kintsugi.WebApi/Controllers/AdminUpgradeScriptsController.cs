using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.ScriptApproval.Commands.AdoptApprovedScript;
using Kintsugi.Application.ScriptApproval.Commands.ImportApprovedScriptsFromSource;
using Kintsugi.Application.ScriptApproval.Queries.GetApprovedScripts;
using Kintsugi.Application.UpgradePaths.Commands.TakeServerWrittenScript;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The Upgrade Scripts screen: every upgrade script an agent could run, whether a human has
/// approved it, and the three human decisions that change that.
/// </summary>
/// <remarks>
/// <para>
/// Replaces <c>Pages/UpgradeScripts.cshtml.cs</c>. Its handlers were page handlers rather than API
/// routes deliberately, and with more force than the Clients page's: <c>Program.cs</c> exempts all
/// of <c>/api</c> from the sign-in gate, so an API route would be triggerable by anyone who could
/// reach the server — and these routes change what agents execute as root.
/// <see cref="RequireAdminSessionAttribute"/> on the class is what now carries that, and it is the
/// only thing that does.
/// </para>
/// <para>
/// Every route here returns the whole screen's state, not just its own outcome. Adopting a script,
/// taking a newer one, or importing the approved corpus all change which rows are signed, which
/// have an upstream counterpart and which are still awaiting review — the page handlers reloaded
/// everything before rendering for that reason, and the client needs the same.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/upgrade-scripts")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminUpgradeScriptsController : ControllerBase
{
    private readonly ISender _sender;

    public AdminUpgradeScriptsController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet]
    [ProducesResponseType(typeof(UpgradeScriptsViewDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UpgradeScriptsViewDto>> Get(CancellationToken cancellationToken) =>
        Ok(new UpgradeScriptsViewDto(await LoadOverviewAsync(cancellationToken)));

    /// <summary>Brings this server up to date with the approval repository's default branch.</summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(UpgradeScriptsViewDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UpgradeScriptsViewDto>> Refresh(CancellationToken cancellationToken)
    {
        ImportApprovedScriptsResultDto? importResult = null;
        string? refreshError = null;

        try
        {
            importResult = await _sender.Send(new ImportApprovedScriptsFromSourceCommand(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only reachable when the repository itself could not be read — a malformed individual
            // entry comes back as a rejection in the result rather than thrown.
            refreshError = ex.Message;
        }

        return Ok(new UpgradeScriptsViewDto(
            await LoadOverviewAsync(cancellationToken),
            ImportResult: importResult,
            RefreshError: refreshError));
    }

    /// <summary>
    /// Takes one approved script onto one local upgrade path.
    /// </summary>
    /// <remarks>
    /// Per-row and human-pressed, deliberately — see <see cref="AdoptApprovedScriptCommand"/> for
    /// why a refresh does not do this by itself. A merge to the approval repository's default
    /// branch is enough to <em>offer</em> new executable content to every server that refreshes;
    /// this call is the last human decision before agents run it as root.
    /// </remarks>
    [HttpPost("adopt")]
    [ProducesResponseType(typeof(UpgradeScriptsViewDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UpgradeScriptsViewDto>> Adopt(
        [FromBody] AdoptApprovedScriptCommand command, CancellationToken cancellationToken)
    {
        AdoptApprovedScriptResultDto? adopted = null;
        string? adoptError = null;

        try
        {
            adopted = await _sender.Send(command, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            adoptError = ex.Message;
        }

        return Ok(new UpgradeScriptsViewDto(
            await LoadOverviewAsync(cancellationToken),
            Adopted: adopted,
            AdoptError: adoptError));
    }

    /// <summary>
    /// Puts the script this build writes for one package-manager row onto it, unsigned.
    /// </summary>
    /// <remarks>
    /// Per-row and human-pressed for the reason <c>RegisterApplicationsCommandHandler</c> no longer
    /// does it in the background: replacing the content of a signed row is replacing what the
    /// fleet's agents execute, and that is a decision rather than a side effect of a deployment.
    /// </remarks>
    [HttpPost("take-server-script")]
    [ProducesResponseType(typeof(UpgradeScriptsViewDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UpgradeScriptsViewDto>> TakeServerScript(
        [FromBody] TakeServerWrittenScriptCommand command, CancellationToken cancellationToken)
    {
        TakeServerWrittenScriptResultDto? took = null;
        string? takeError = null;

        try
        {
            took = await _sender.Send(command, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            takeError = ex.Message;
        }

        return Ok(new UpgradeScriptsViewDto(
            await LoadOverviewAsync(cancellationToken),
            TookServerScript: took,
            TakeServerScriptError: takeError));
    }

    private Task<UpgradeScriptsOverviewDto> LoadOverviewAsync(CancellationToken cancellationToken) =>
        _sender.Send(new GetUpgradeScriptsOverviewQuery(), cancellationToken);
}

/// <summary>The screen's state, plus whatever the action that produced this response did.</summary>
/// <remarks>
/// The outcome fields are all null on a plain read. They are reported as data rather than as HTTP
/// failures because none of them means the request failed: a GitHub outage must not stop a reviewed
/// script from patching the fleet it was reviewed for, so a failed publish is a note beside a
/// working screen.
/// </remarks>
public record UpgradeScriptsViewDto(
    UpgradeScriptsOverviewDto Overview,
    ImportApprovedScriptsResultDto? ImportResult = null,
    string? RefreshError = null,
    AdoptApprovedScriptResultDto? Adopted = null,
    string? AdoptError = null,
    TakeServerWrittenScriptResultDto? TookServerScript = null,
    string? TakeServerScriptError = null);
