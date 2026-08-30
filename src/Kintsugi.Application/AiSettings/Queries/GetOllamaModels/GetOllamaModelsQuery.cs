using MediatR;

namespace Kintsugi.Application.AiSettings.Queries.GetOllamaModels;

public record GetOllamaModelsQuery(string BaseUrl) : IRequest<IReadOnlyList<string>>;
