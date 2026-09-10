using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.PatchFailures.Queries.GetPatchFailures;

public class GetPatchFailuresQueryHandler : IRequestHandler<GetPatchFailuresQuery, IReadOnlyList<PatchFailureDto>>
{
    private readonly IPatchFailureRepository _repository;

    public GetPatchFailuresQueryHandler(IPatchFailureRepository repository)
    {
        _repository = repository;
    }

    public Task<IReadOnlyList<PatchFailureDto>> Handle(GetPatchFailuresQuery request, CancellationToken cancellationToken) =>
        _repository.GetAllAsync(cancellationToken);
}
