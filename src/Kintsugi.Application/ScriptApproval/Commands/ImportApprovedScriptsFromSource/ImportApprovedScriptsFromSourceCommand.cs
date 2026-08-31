using MediatR;

namespace Kintsugi.Application.ScriptApproval.Commands.ImportApprovedScriptsFromSource;

/// <summary>
/// Reads the shared approval repository's default branch and brings this server up to date with it:
/// stores every well-formed approval entry, and signs any local upgrade path whose script is already
/// byte-for-byte one of them.
///
/// Backs the Upgrade Scripts page's "Refresh scripts" action. Like the Clients page's refresh, this is
/// a Razor Page handler rather than an API route — see <c>ClientsModel.OnPostRefreshAsync</c> for the
/// reasoning, which applies with more force here: an <c>/api</c> route would be exempt from the
/// sign-in gate, and this one changes what agents execute.
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
