using MediatR;

namespace Kintsugi.Application.AiSettings.Queries.GetAiAgentSettings;

public record GetAiAgentSettingsQuery : IRequest<AiAgentSettingsDto>;
