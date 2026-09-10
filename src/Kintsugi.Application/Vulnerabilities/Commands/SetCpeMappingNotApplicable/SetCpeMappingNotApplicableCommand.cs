using MediatR;

namespace Kintsugi.Application.Vulnerabilities.Commands.SetCpeMappingNotApplicable;

/// <summary>Records that a subject has no meaningful CPE — an in-house tool, a package manager's
/// own row, a bundle NVD has never heard of. A decision rather than a gap, so it leaves the "not
/// assessed" count.</summary>
public record SetCpeMappingNotApplicableCommand(Guid Id, string? Notes) : IRequest<Unit>;
