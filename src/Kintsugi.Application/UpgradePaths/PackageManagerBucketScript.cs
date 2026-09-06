using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// The one script a package manager's bucket holds, and the signature that covers it — what every
/// row written into that bucket gets, whether it is being seeded by an inventory report
/// (<c>RegisterApplicationsCommandHandler</c>) or by "Find Upgrade Paths"
/// (<c>ResearchApplicationUpgradePathCommandHandler</c>).
/// </summary>
/// <remarks>
/// <para>
/// A package-manager bucket is meant to hold exactly one script: every application the manager
/// handles runs the same bytes, one review covers all of them, and the Upgrade Scripts screen lists
/// the bucket once (<c>LocalScriptDto</c>). Both writers used to ask the <em>builder</em> what to
/// write, and that is what broke the invariant. A signed row is deliberately never rewritten by a
/// deployment, so after an edit to a <c>*UpgradeScript.Build</c> body the bucket's reviewed rows kept
/// the old text — while every row seeded <em>after</em> the deployment got the new one, unsigned,
/// because no signature existed for those bytes. The result was two Homebrew entries on the screen
/// (118 applications on the reviewed text, 4 on the builder's) and, worse, four applications that
/// were quietly not patching: an unsigned row is inert, and nothing said so except a second row on a
/// page nobody was looking at.
/// </para>
/// <para>
/// So the source of truth for a bucket is <em>what the bucket already runs</em>, not what this build
/// would write. If any row in the bucket is signed, a new or unsigned row takes that text and that
/// signature — it joins the fleet's reviewed script at once and patches at once. Only a bucket with
/// nothing signed in it is written from the builder, which is the very first script per manager,
/// and the one a human still has to review. The builder's newer text reaches a reviewed bucket by
/// exactly one route, <c>TakeServerWrittenScriptCommand</c>, which moves every row at once and leaves
/// them unsigned together — so the bucket is never half on one revision and half on another.
/// </para>
/// <para>
/// Signed rows are still never touched here. A bucket can only come to hold two <em>signed</em>
/// texts if a human pastes a different script onto one row and then signs it — two deliberate acts
/// — and that is respected rather than undone: the screen shows two entries, honestly. Should that
/// happen, the text on the most rows is what new rows join, because it is what the fleet mostly runs.
/// </para>
/// </remarks>
public sealed record PackageManagerBucketScript(string Script, string? Signature)
{
    public static async Task<PackageManagerBucketScript> ResolveAsync(
        IUpgradePathRepository upgradePaths, RecognizedPackageManager manager, CancellationToken cancellationToken)
    {
        var platform = PlatformBucket.ForPackageManager(manager.Name);
        var reviewed = await upgradePaths.GetSignedPackageManagerScriptAsync(platform, cancellationToken);
        if (reviewed is not null)
        {
            return reviewed;
        }

        // Nothing in this bucket has been reviewed yet, so the builder's text is the only candidate.
        // The content lookup is kept for the one case it still answers: a bucket emptied and re-seeded
        // while another row somewhere holds these exact bytes signed.
        var script = manager.BuildScript();
        return new PackageManagerBucketScript(
            script, await upgradePaths.FindExistingSignatureForScriptAsync(script, cancellationToken));
    }
}
