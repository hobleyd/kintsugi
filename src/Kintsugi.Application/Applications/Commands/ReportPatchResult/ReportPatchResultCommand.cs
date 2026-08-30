using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Applications.Commands.ReportPatchResult;

/// <summary>
/// Records that an agent successfully patched one already-installed application on a host to
/// <see cref="NewVersion"/> — sent right after a patch cycle applies an upgrade, so the server's
/// record of what's installed reflects it immediately instead of waiting on that host's next
/// full inventory report (see <see cref="RegisterApplications.RegisterApplicationsCommand"/>).
/// There is deliberately no "failed" counterpart: a failed patch leaves the previously reported
/// version exactly as it was, which is already correct.
/// </summary>
public record ReportPatchResultCommand(string SerialNumber, string ApplicationName, string NewVersion)
    : IRequest<Unit>, IAgentScopedRequest;
