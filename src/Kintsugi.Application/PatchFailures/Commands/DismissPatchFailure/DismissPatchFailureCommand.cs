using MediatR;

namespace Kintsugi.Application.PatchFailures.Commands.DismissPatchFailure;

/// <summary>
/// Clears one failure off the Failed Updates screen by hand — for a failure that will not recur
/// (the host has been rebuilt, the application uninstalled) and so will never be closed by a
/// success report the way <c>ReportPatchResultCommandHandler</c> closes the rest.
/// </summary>
/// <remarks>
/// The row is kept and marked, not deleted: the screen's status filter can still show it, and a
/// dismissed failure that starts happening again is *reopened* rather than duplicated, keeping its
/// count and its "failing since" date intact (see <c>PatchFailure.Reopen</c>).
/// </remarks>
public record DismissPatchFailureCommand(Guid Id) : IRequest<Unit>;
