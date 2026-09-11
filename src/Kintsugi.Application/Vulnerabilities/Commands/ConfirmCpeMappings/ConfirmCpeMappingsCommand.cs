using MediatR;

namespace Kintsugi.Application.Vulnerabilities.Commands.ConfirmCpeMappings;

/// <summary>
/// Accepts, in one go, the vendor and product each of these subjects already carries — the mapping
/// queue's bulk Confirm, applied to whatever the reviewer has ticked.
/// </summary>
/// <remarks>
/// Deliberately takes no vendor or product of its own. A bulk confirmation is "these proposals are
/// right", not "these subjects are all this product", and the single-row
/// <c>ConfirmCpeMappingCommand</c> remains the only way to assert a pair a human typed.
/// </remarks>
public record ConfirmCpeMappingsCommand(IReadOnlyList<Guid> Ids) : IRequest<BulkCpeMappingResultDto>;
