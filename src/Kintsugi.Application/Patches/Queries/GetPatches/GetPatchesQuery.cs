using MediatR;

namespace Kintsugi.Application.Patches.Queries.GetPatches;

public record GetPatchesQuery : IRequest<IReadOnlyList<PatchDto>>;
