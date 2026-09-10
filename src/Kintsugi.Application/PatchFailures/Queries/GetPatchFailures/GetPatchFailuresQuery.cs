using MediatR;

namespace Kintsugi.Application.PatchFailures.Queries.GetPatchFailures;

/// <summary>
/// Every reported patch failure, fleet-wide — what the Failed Updates screen renders. Unfiltered:
/// the screen offers its own status filter (outstanding by default), and the whole set is bounded
/// by how many distinct things are broken rather than by fleet size, because repeats fold into one
/// row per (host, application).
/// </summary>
public record GetPatchFailuresQuery : IRequest<IReadOnlyList<PatchFailureDto>>;
