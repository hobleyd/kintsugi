using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AiSettings.Queries.GetAiCatalog;

public record GetAiCatalogQuery(bool ForceRefresh) : IRequest<AiCatalogDto>;

public class GetAiCatalogQueryHandler : IRequestHandler<GetAiCatalogQuery, AiCatalogDto>
{
    private readonly IAiModelCatalog _catalog;

    public GetAiCatalogQueryHandler(IAiModelCatalog catalog)
    {
        _catalog = catalog;
    }

    public Task<AiCatalogDto> Handle(GetAiCatalogQuery request, CancellationToken cancellationToken) =>
        _catalog.GetAsync(request.ForceRefresh, cancellationToken);
}
