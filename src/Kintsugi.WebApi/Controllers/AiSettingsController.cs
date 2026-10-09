using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.AiSettings.Commands.DeleteAiConnection;
using Kintsugi.Application.AiSettings.Commands.DeleteAiFeatureRoute;
using Kintsugi.Application.AiSettings.Commands.SaveAiConnection;
using Kintsugi.Application.AiSettings.Commands.SetAiFeatureRoute;
using Kintsugi.Application.AiSettings.Commands.TestAiConnection;
using Kintsugi.Application.AiSettings.Commands.UpdateAiAgentSettings;
using Kintsugi.Application.AiSettings.Commands.UpdateWebSearchSettings;
using Kintsugi.Application.AiSettings.Queries.GetAiCatalog;
using Kintsugi.Application.AiSettings.Queries.GetAiConnections;
using Kintsugi.Application.AiSettings.Queries.GetAiAgentSettings;
using Kintsugi.Application.AiSettings.Queries.GetClaudeAgentSdkStatus;
using Kintsugi.Application.AiSettings.Queries.GetGooseCliStatus;
using Kintsugi.Application.AiSettings.Queries.GetOllamaModels;
using Kintsugi.Application.Common.Interfaces;

using Kintsugi.Domain.Enums;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

[ApiController]
[Route("api/ai-settings")]
[Produces("application/json")]
// Applied to the class rather than each action: every route here is driven by the Settings pages'
// JavaScript and none of them is an agent route, so the safe posture is the default and a route
// added later inherits it. Nothing else would have stopped it being anonymous — nginx's
// client-certificate regex is an exact match that never covers /api/ai-settings, and Program.cs
// exempts all of /api from the sign-in gate. Repointing the AI provider is a configuration change
// that decides which endpoint every generated upgrade script comes from.
[RequireAdminSession]
public class AiSettingsController : ControllerBase
{
    private readonly ISender _sender;

    public AiSettingsController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Gets the configured AI agent connection settings. The API key itself is never returned.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(AiAgentSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AiAgentSettingsDto>> Get(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetAiAgentSettingsQuery(), cancellationToken));

    /// <summary>Creates or updates the AI agent connection settings.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(AiAgentSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AiAgentSettingsDto>> Update(UpdateAiAgentSettingsCommand command, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(command, cancellationToken));

    /// <summary>Lists the model names installed on a local Ollama endpoint, to populate the model dropdown in Settings.</summary>
    [HttpGet("ollama-models")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<IReadOnlyList<string>>> GetOllamaModels([FromQuery] string baseUrl, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetOllamaModelsQuery(baseUrl), cancellationToken));

    /// <summary>Checks whether a `goose serve` instance can be reached (optionally at a specific
    /// base URL) and reports the connected agent's version, to power a status check in
    /// Settings.</summary>
    [HttpGet("goose-cli-status")]
    [ProducesResponseType(typeof(GooseCliStatus), StatusCodes.Status200OK)]
    public async Task<ActionResult<GooseCliStatus>> GetGooseCliStatus([FromQuery] string? endpoint, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetGooseCliStatusQuery(endpoint), cancellationToken));

    /// <summary>Checks that the Claude Agent SDK is usable from this server — that the `claude`
    /// binary is installed and that the stored OAuth token still authenticates — to power a status
    /// check in Settings. It takes no parameters on purpose: the token is never sent to the
    /// browser, so the probe reads the stored one rather than one supplied by the caller.</summary>
    /// <summary>Routed mode's connections and per-feature routes.</summary>
    [HttpGet("routing")]
    [ProducesResponseType(typeof(AiRoutingDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AiRoutingDto>> GetRouting(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetAiRoutingQuery(), cancellationToken));

    /// <summary>Creates a connection; the body's <c>id</c> is ignored.</summary>
    [HttpPost("connections")]
    [ProducesResponseType(typeof(AiConnectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AiConnectionDto>> CreateConnection(SaveAiConnectionCommand command, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(command with { Id = null }, cancellationToken));

    /// <summary>Updates a connection. A blank <c>apiKey</c> keeps the stored one.</summary>
    [HttpPut("connections/{id:guid}")]
    [ProducesResponseType(typeof(AiConnectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AiConnectionDto>> UpdateConnection(Guid id, SaveAiConnectionCommand command, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(command with { Id = id }, cancellationToken));

    /// <summary>Deletes a connection; refused (409) while any feature is routed to it.</summary>
    [HttpDelete("connections/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteConnection(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new DeleteAiConnectionCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>Sends one tiny prompt down a stored connection with <paramref name="model"/>.</summary>
    [HttpPost("connections/{id:guid}/test")]
    [ProducesResponseType(typeof(AiConnectionTestResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AiConnectionTestResultDto>> TestConnection(Guid id, [FromQuery] string model, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new TestAiConnectionCommand(id, model), cancellationToken));

    [HttpPut("routes/{feature}")]
    [ProducesResponseType(typeof(AiFeatureRouteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AiFeatureRouteDto>> SetRoute(AiFeature feature, SetAiFeatureRouteBody body, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new SetAiFeatureRouteCommand(feature, body.ConnectionId, body.Model), cancellationToken));

    [HttpDelete("routes/{feature}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteRoute(AiFeature feature, CancellationToken cancellationToken)
    {
        await _sender.Send(new DeleteAiFeatureRouteCommand(feature), cancellationToken);
        return NoContent();
    }

    /// <summary>The search service behind Kintsugi's own web_search tool.</summary>
    [HttpPut("web-search")]
    [ProducesResponseType(typeof(AiAgentSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AiAgentSettingsDto>> UpdateWebSearch(UpdateWebSearchSettingsCommand command, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(command, cancellationToken));

    /// <summary>The models.dev catalog, trimmed; <paramref name="refresh"/> refetches now.</summary>
    [HttpGet("catalog")]
    [ProducesResponseType(typeof(AiCatalogDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AiCatalogDto>> GetCatalog([FromQuery] bool refresh, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetAiCatalogQuery(refresh), cancellationToken));

    [HttpGet("claude-agent-sdk-status")]
    [ProducesResponseType(typeof(ClaudeAgentSdkStatus), StatusCodes.Status200OK)]
    public async Task<ActionResult<ClaudeAgentSdkStatus>> GetClaudeAgentSdkStatus(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetClaudeAgentSdkStatusQuery(), cancellationToken));
}

/// <summary>The body of <c>PUT routes/{feature}</c>; the feature is in the path.</summary>
public record SetAiFeatureRouteBody(Guid ConnectionId, string Model);
