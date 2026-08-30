using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Patches.Queries.GetPatches;

public class GetPatchesQueryHandler : IRequestHandler<GetPatchesQuery, IReadOnlyList<PatchDto>>
{
    private readonly IPatchRepository _patchRepository;

    public GetPatchesQueryHandler(IPatchRepository patchRepository)
    {
        _patchRepository = patchRepository;
    }

    public async Task<IReadOnlyList<PatchDto>> Handle(GetPatchesQuery request, CancellationToken cancellationToken)
    {
        var patches = await _patchRepository.GetAllAsync(cancellationToken);
        return patches.Select(PatchDto.FromEntity).ToList();
    }
}
