using MediatR;

namespace Kintsugi.Application.ScriptApproval.Commands.ImportApprovedScriptsFromSource;

/// <summary>
/// Reads the shared approval repository's default branch and brings this server up to date with it:
/// stores every well-formed approval entry, and signs any local upgrade path whose script is already
/// byte-for-byte one of them.
///
/// Backs the Upgrade Scripts screen's "Refresh scripts" action, via
/// <c>AdminUpgradeScriptsController.Refresh</c>. This used to be a Razor Page handler specifically so
/// that it would <em>not</em> be an <c>/api</c> route, since <c>Program.cs</c> exempts all of
/// <c>/api</c> from the sign-in gate and this action changes what agents execute. With the admin UI
/// now a client rather than a server-rendered page there is no page handler to use, so what carries
/// that reasoning is <c>[RequireAdminSession]</c> on the controller — and nothing else does.
/// </summary>
public record ImportApprovedScriptsFromSourceCommand : IRequest<ImportApprovedScriptsResultDto>;

/// <param name="Imported">Entries newly stored on this server.</param>
/// <param name="AlreadyKnown">Entries this server had already read in a previous refresh.</param>
/// <param name="Blessed">Local upgrade paths that gained a signature because their existing script
/// turned out to be approved content. No script text changed for any of these — the bytes were
/// already here — which is why this half needs no human decision.</param>
/// <param name="Rejected">Every entry passed over, with the reason. Shown rather than counted:
/// a signature that doesn't verify and a corpus that is simply small look identical otherwise.</param>
public record ImportApprovedScriptsResultDto(
    string Repository,
    string? CommitSha,
    int Imported,
    int AlreadyKnown,
    IReadOnlyList<BlessedUpgradePathDto> Blessed,
    IReadOnlyList<string> Rejected);

public record BlessedUpgradePathDto(string ApplicationName, string Platform, string SignerFingerprint);
