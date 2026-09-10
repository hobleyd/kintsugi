using MediatR;

namespace Kintsugi.Application.Vulnerabilities.Commands.ResetCpeMapping;

/// <summary>Returns a subject to the mapping queue — for one marked not-applicable in error, or a
/// suggestion a reviewer rejected without a better one to hand.</summary>
public record ResetCpeMappingCommand(Guid Id) : IRequest<Unit>;
