using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AiSettings.Queries.GetClaudeAgentSdkStatus;

public class GetClaudeAgentSdkStatusQueryHandler : IRequestHandler<GetClaudeAgentSdkStatusQuery, ClaudeAgentSdkStatus>
{
    private readonly IClaudeAgentSdkClient _client;
    private readonly IAiAgentSettingsRepository _settings;

    public GetClaudeAgentSdkStatusQueryHandler(IClaudeAgentSdkClient client, IAiAgentSettingsRepository settings)
    {
        _client = client;
        _settings = settings;
    }

    public async Task<ClaudeAgentSdkStatus> Handle(GetClaudeAgentSdkStatusQuery request, CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken);
        return await _client.CheckAvailabilityAsync(settings?.ApiKey, cancellationToken);
    }
}
