using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Hosts.Commands.ReportOperatingSystemPatched;

/// <summary>
/// Records that an agent successfully installed a pending macOS update on this host — sent right
/// after the root daemon's install-via-daemon handoff reports success, so the host's pending
/// flag and target version clear immediately instead of waiting on its next check-in (boot,
/// daily, or on demand) to re-derive them from a fresh <c>softwareupdate -l</c> run.
/// </summary>
public record ReportOperatingSystemPatchedCommand(string SerialNumber) : IRequest<Unit>, IAgentScopedRequest;
