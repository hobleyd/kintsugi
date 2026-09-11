using MediatR;

namespace Kintsugi.Application.Vulnerabilities.Commands.ResetCpeMappings;

/// <summary>Returns several subjects to the mapping queue at once — the queue's bulk Clear.</summary>
public record ResetCpeMappingsCommand(IReadOnlyList<Guid> Ids) : IRequest<BulkCpeMappingResultDto>;
