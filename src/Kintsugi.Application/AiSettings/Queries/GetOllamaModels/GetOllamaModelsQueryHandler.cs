using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AiSettings.Queries.GetOllamaModels;

public class GetOllamaModelsQueryHandler : IRequestHandler<GetOllamaModelsQuery, IReadOnlyList<string>>
{
    private readonly IOllamaModelsClient _client;

    public GetOllamaModelsQueryHandler(IOllamaModelsClient client)
    {
        _client = client;
    }

    public Task<IReadOnlyList<string>> Handle(GetOllamaModelsQuery request, CancellationToken cancellationToken) =>
        _client.GetAvailableModelsAsync(request.BaseUrl, cancellationToken);
}
