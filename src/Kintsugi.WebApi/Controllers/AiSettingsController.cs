using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.AiSettings.Commands.UpdateAiAgentSettings;
using Kintsugi.Application.AiSettings.Queries.GetAiAgentSettings;
using Kintsugi.Application.AiSettings.Queries.GetGooseCliStatus;
using Kintsugi.Application.AiSettings.Queries.GetOllamaModels;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.WebApi.Controllers;

[ApiController]
[Route("api/ai-settings")]
[Produces("application/json")]
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
}
