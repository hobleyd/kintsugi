using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AiSettings.Queries.GetAiAgentSettings;

public class GetAiAgentSettingsQueryHandler : IRequestHandler<GetAiAgentSettingsQuery, AiAgentSettingsDto>
{
    private readonly IAiAgentSettingsRepository _repository;

    public GetAiAgentSettingsQueryHandler(IAiAgentSettingsRepository repository)
    {
        _repository = repository;
    }

    public async Task<AiAgentSettingsDto> Handle(GetAiAgentSettingsQuery request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);
        return settings is null ? AiAgentSettingsDto.NotConfigured() : AiAgentSettingsDto.FromEntity(settings);
    }
}
