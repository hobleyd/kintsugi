using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.PatchFailures.Commands.ReportPatchFailure;

/// <summary>
/// Records that an upgrade failed *while it was running* on a host — the counterpart to
/// <see cref="Applications.Commands.ReportPatchResult.ReportPatchResultCommand"/>, which reports
/// the success. Sent by the process that actually ran the script, which is the only side that saw
/// it fail: the root daemon/service for an AI-researched script, the per-user process for a
/// package-manager one (see each agent's <c>patch_cycle::run_patches</c>).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow: this is for a script or command that ran and failed, not for every reason a
/// patch did not happen. An application with no signed, patchable path, or a Homebrew row the
/// daemon refuses to run as root, are configuration problems rather than bugs in a script, and
/// filling the Failed Updates screen with rows the AI cannot fix would make the ones it can invisible.
/// OS updates are out of scope for the same reason — there is no script for a human or the AI to fix.
/// </para>
/// <para>
/// <see cref="FailedUtc"/> comes from the host's own clock rather than being stamped on arrival: an
/// agent that patched overnight and could not reach the server until morning would otherwise report
/// the failure as having happened at the moment the network came back.
/// </para>
/// <para>
/// Notice what is *not* here: the platform bucket. It is resolved server-side — see
/// <see cref="ReportPatchFailureCommandHandler"/> and the remarks on
/// <c>Kintsugi.Domain.Entities.PatchFailure</c> for why an agent must not send it.
/// </para>
/// <para>
/// Hand-mirrored as <c>ReportPatchFailureRequest</c> in all three agents' <c>upgrade.rs</c>; see
/// <c>.claude/rules/hand-mirrored-dtos.md</c>.
/// </para>
/// </remarks>
public record ReportPatchFailureCommand(
    string SerialNumber,
    string ApplicationName,
    string? InstalledVersion,
    string? AttemptedVersion,
    DateTimeOffset FailedUtc,
    string Details) : IRequest<Unit>, IAgentScopedRequest;
