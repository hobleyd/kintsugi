using MediatR;

namespace Kintsugi.Application.UpgradePaths.Commands.TakeServerWrittenScript;

/// <summary>
/// Replaces one package-manager script — on every row under <paramref name="Platform"/> that holds
/// it — with the one this server's current build writes, and leaves those rows unsigned for review.
/// </summary>
/// <remarks>
/// The deliberate half of a decision that used to happen by itself. A signed row keeps its reviewed
/// script across server upgrades (see <c>RegisterApplicationsCommandHandler</c>), so when an edit to
/// a <c>*UpgradeScript.Build</c> body means this build would write something different, the row goes
/// on running the text a human approved until somebody chooses otherwise. This command is that
/// choice, pressed on the Upgrade Scripts page.
///
/// It is addressed by (bucket, content hash) rather than by row because that is what the page shows
/// and what the reviewer decided about: a package-manager script is byte-identical for every
/// application the manager handles, the page lists it once (<c>LocalScriptDto</c>), and taking the
/// newer text for one application while its siblings kept the old would leave one manager's bucket
/// running two revisions with nothing to say which was reviewed. The hash comes from that listing,
/// so a stale page names a text that is no longer there and gets a NotFound rather than acting on
/// whatever replaced it.
///
/// It does not sign. The rows land unsigned, which stops the new text reaching a single host until
/// it has been read — the point of the exercise — and one "Sign Script" then covers every row
/// sharing those exact bytes via <c>FindExistingSignatureForScriptAsync</c>.
/// </remarks>
/// <param name="Sha256">The <c>ScriptContentHash</c> of the script to replace, as
/// <c>LocalScriptDto.Sha256</c> reports it.</param>
public record TakeServerWrittenScriptCommand(string Platform, string Sha256)
    : IRequest<TakeServerWrittenScriptResultDto>;

/// <param name="Changed">How many rows took the new text. Zero when every row already held exactly
/// this build's script, which is the normal outcome of pressing it twice — reported rather than
/// treated as an error: nothing is wrong, and a row that is already current is the state being
/// aimed at.</param>
public record TakeServerWrittenScriptResultDto(string Platform, int Changed);
