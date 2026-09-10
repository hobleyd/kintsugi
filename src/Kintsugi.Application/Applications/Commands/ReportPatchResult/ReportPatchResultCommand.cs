using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Applications.Commands.ReportPatchResult;

/// <summary>
/// Records that an agent successfully patched one already-installed application on a host to
/// <see cref="NewVersion"/> — sent right after a patch cycle applies an upgrade, so the server's
/// record of what's installed reflects it immediately instead of waiting on that host's next
/// full inventory report (see <see cref="RegisterApplications.RegisterApplicationsCommand"/>).
/// A failed patch leaves the previously reported version exactly as it was, which is already
/// correct — so the failure is reported separately, to
/// <see cref="PatchFailures.Commands.ReportPatchFailure.ReportPatchFailureCommand"/>, which is
/// about the script rather than about the installed version. This command closes any failure that
/// one left outstanding for the same (host, application).
/// </summary>
public record ReportPatchResultCommand(string SerialNumber, string ApplicationName, string NewVersion)
    : IRequest<Unit>, IAgentScopedRequest;
