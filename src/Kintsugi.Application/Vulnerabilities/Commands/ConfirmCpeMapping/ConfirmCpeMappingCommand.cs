using MediatR;

namespace Kintsugi.Application.Vulnerabilities.Commands.ConfirmCpeMapping;

/// <summary>
/// Accepts a CPE vendor and product for one subject, which is the only thing that makes it
/// eligible for assessment.
/// </summary>
/// <param name="Vendor">Possibly corrected by the reviewer rather than the one that was suggested
/// — the whole point of the review step.</param>
public record ConfirmCpeMappingCommand(Guid Id, string Vendor, string Product) : IRequest<Unit>;
