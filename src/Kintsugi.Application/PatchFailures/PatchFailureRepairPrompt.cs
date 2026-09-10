using System.Text;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.PatchFailures;

/// <summary>
/// Turns a reported failure and the script that produced it into the extra instructions an AI
/// repair run is given — what the Failed Updates screen's fix panel loads in place of the plain
/// research prompt.
/// </summary>
public static class PatchFailureRepairPrompt
{
    /// <summary>
    /// The repair brief, to be **appended to** the default research prompt rather than replacing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Appended, and that is the whole design of this. The default prompt is where the CLI contract
    /// (<c>--update-version</c> / <c>--update</c>), the "version checks run on the Linux API server,
    /// the update runs on the managed host" split, and the response's JSON shape are all stated —
    /// see <c>AiUpgradePathResearchClient.BuildScriptGenerationPrompt</c>. A repair prompt that
    /// replaced it would get back a fixed script in a shape
    /// <c>ResearchApplicationUpgradePathCommandHandler</c> cannot parse, or one that no longer
    /// honours the contract every agent invokes it by — a script that is "fixed" and unrunnable.
    /// </para>
    /// <para>
    /// The script sent is whatever the row holds *now*, not a copy taken when the failure was
    /// recorded. That is the one an agent would run on its next cycle, so it is the one worth
    /// fixing; a snapshot would send the AI something that may since have been edited or replaced.
    /// </para>
    /// </remarks>
    public static string Build(PatchFailure failure, UpgradePath? path, string hostname)
    {
        var brief = new StringBuilder();

        brief.AppendLine();
        brief.AppendLine("## Repair an upgrade script that failed on a managed host");
        brief.AppendLine();
        brief.AppendLine(
            "The script below is the one currently stored for this application. It was run on a managed host and it "
            + "failed. Your job is to work out why and return a corrected script, in exactly the format described above.");
        brief.AppendLine();
        brief.AppendLine($"- Host: {hostname}");
        brief.AppendLine($"- Application: {failure.ApplicationName}");
        if (failure.Platform is not null)
        {
            brief.AppendLine($"- Platform: {failure.Platform}");
        }
        if (failure.InstalledVersion is not null)
        {
            brief.AppendLine($"- Installed version at the time: {failure.InstalledVersion}");
        }
        if (failure.AttemptedVersion is not null)
        {
            brief.AppendLine($"- Version it was upgrading to: {failure.AttemptedVersion}");
        }
        brief.AppendLine($"- First failed: {failure.FirstFailedUtc:u}");
        brief.AppendLine($"- Last failed: {failure.LastFailedUtc:u}");
        brief.AppendLine($"- Failed attempts so far: {failure.FailureCount}");
        brief.AppendLine();

        brief.AppendLine("### What the run reported");
        brief.AppendLine();
        brief.AppendLine("```");
        brief.AppendLine(failure.Details.Trim());
        brief.AppendLine("```");
        brief.AppendLine();

        if (!string.IsNullOrWhiteSpace(path?.Script))
        {
            brief.AppendLine("### The script as it stands now");
            brief.AppendLine();
            brief.AppendLine("```");
            brief.AppendLine(path.Script!.TrimEnd());
            brief.AppendLine("```");
            brief.AppendLine();
        }
        else if (!string.IsNullOrWhiteSpace(path?.Command))
        {
            brief.AppendLine("### The command as it stands now");
            brief.AppendLine();
            brief.AppendLine("```");
            brief.AppendLine(path.Command!.Trim());
            brief.AppendLine("```");
            brief.AppendLine();
        }
        else
        {
            brief.AppendLine(
                "There is no stored script for this application any more, so write a fresh one that avoids the failure above.");
            brief.AppendLine();
        }

        brief.AppendLine(
            "Keep the parts that work. Change what the output above shows to be wrong, and say in the notes what you "
            + "changed and why, so the person reviewing this before signing it can check your reasoning against the "
            + "error rather than re-reading the whole script.");

        return brief.ToString();
    }
}
