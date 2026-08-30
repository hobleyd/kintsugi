using MediatR;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.AiSettings.Commands.UpdateAiAgentSettings;

/// <summary>A blank <paramref name="ApiKey"/> leaves any previously stored key untouched.</summary>
public record UpdateAiAgentSettingsCommand(
    AiProvider Provider,
    string? ApiKey,
    string? BaseUrl,
    string? Model,
    bool IsEnabled) : IRequest<AiAgentSettingsDto>;
