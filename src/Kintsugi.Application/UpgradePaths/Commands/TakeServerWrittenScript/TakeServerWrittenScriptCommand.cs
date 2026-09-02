using MediatR;

namespace Kintsugi.Application.UpgradePaths.Commands.TakeServerWrittenScript;

/// <summary>
/// Replaces one package-manager row's script with the one this server's current build writes, and
/// leaves it unsigned for review.
/// </summary>
/// <remarks>
/// The deliberate half of a decision that used to happen by itself. A signed row keeps its reviewed
/// script across server upgrades (see <c>RegisterApplicationsCommandHandler</c>), so when an edit to
/// a <c>*UpgradeScript.Build</c> body means this build would write something different, the row goes
/// on running the text a human approved until somebody chooses otherwise. This command is that
/// choice, pressed per row on the Upgrade Scripts page.
///
/// It does not sign. The row lands unsigned, which stops the new text reaching a single host until
/// it has been read — the point of the exercise — and one "Sign Script" then covers every other row
/// sharing those exact bytes via <c>FindExistingSignatureForScriptAsync</c>.
/// </remarks>
public record TakeServerWrittenScriptCommand(string ApplicationName, string Platform)
    : IRequest<TakeServerWrittenScriptResultDto>;

/// <param name="Changed">False when the row already held exactly this build's script, which is the
/// normal outcome of pressing it twice. Reported rather than treated as an error: nothing is wrong,
/// and a row that is already current is the state being aimed at.</param>
public record TakeServerWrittenScriptResultDto(string ApplicationName, string Platform, bool Changed);
