using System.Text;
using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Application.ScriptApproval;

/// <summary>
/// How one approval entry describes and names itself in the shared repository: the label a reviewer
/// reads, the filename its script is written under, and the application identifier (if any) worth
/// carrying across.
/// </summary>
/// <remarks>
/// This exists because the row a human happens to press "Sign Script" on is <em>not</em> what the
/// entry is about. A package-manager script is byte-identical for every application that manager
/// handles — that is the whole point of <c>*UpgradeScript.Build</c>, and why one review covers all
/// of them (see <c>FindExistingSignatureForScriptAsync</c>). Publishing it as
/// "Approve ada-url upgrade script" therefore names the entry after whichever application was
/// signed first and reads, wrongly, as though the review were specific to it. An entry that every
/// server may adopt should say what it actually is: the Homebrew script.
///
/// An AI-researched script is the opposite — genuinely per-application — so there the application's
/// own identity is exactly the right label, and its identifier is the most stable form of it
/// (CFBundleIdentifier on macOS, the winget/Chocolatey package id on Windows, the Flatpak/Snap app
/// id on Linux).
/// </remarks>
/// <param name="DisplayName">What the metadata, the commit message and the pull request call this.
/// Deliberately never equal to a real application name for a package-manager entry: adoption
/// candidates are offered by matching an entry's name against a local row's
/// (<c>GetUpgradeScriptsOverviewQueryHandler</c>), and "Homebrew" would match the manager's own
/// self-update row and offer it the per-application script.</param>
/// <param name="FileBaseName">The script's filename without its extension — see
/// <see cref="ApprovedScriptCorpus.ScriptPath"/>.</param>
/// <param name="ApplicationIdentifier">Null for a package-manager entry, where the signing server's
/// note of which application it happened to be reviewing is misleading rather than useful.</param>
public record ApprovedScriptIdentity(string DisplayName, string FileBaseName, string? ApplicationIdentifier)
{
    /// <summary>Whether this entry describes a package manager's shared script rather than one
    /// application's. Callers use it for wording, never for a decision about what may run.</summary>
    public bool IsPackageManagerScript { get; private init; }

    /// <summary>Longest filename stem written, in characters. Well inside git's 255-byte path
    /// component limit while leaving room for a multi-byte name that slugs to something long.</summary>
    private const int MaxFileBaseNameLength = 100;

    /// <summary>Used when a name slugs away to nothing at all (a name made entirely of characters a
    /// path can't carry). The historical filename, so such an entry looks like an old one rather
    /// than like a broken one.</summary>
    private const string FallbackFileBaseName = "script";

    public static ApprovedScriptIdentity For(ScriptApprovalSubmission submission) =>
        For(submission.PlatformBucket, submission.Script, submission.ApplicationName, submission.ApplicationIdentifier);

    public static ApprovedScriptIdentity For(
        string platformBucket, string script, string applicationName, string? applicationIdentifier)
    {
        if (TryPackageManagerIdentity(platformBucket, script, out var identity))
        {
            return identity;
        }

        // An OS bucket, or a package-manager bucket holding something other than that manager's own
        // generated script (a row whose content was hand-edited, say). Either way it is one
        // application's script and is named after that application.
        return new ApprovedScriptIdentity(
            applicationName,
            Slug(applicationIdentifier is { Length: > 0 } id ? id : applicationName),
            applicationIdentifier);
    }

    private static bool TryPackageManagerIdentity(string platformBucket, string script, out ApprovedScriptIdentity identity)
    {
        identity = default!;

        var managerName = PlatformBucket.PackageManagerNameFrom(platformBucket);
        if (managerName is null || !PackageManagerCatalog.TryGet(managerName, out var manager))
        {
            return false;
        }

        // Which of the manager's two scripts this is, decided by comparing the bytes rather than by
        // looking at the row — the row is exactly what must not be trusted here, and the content is
        // what the entry is keyed by anyway. The managed case is tested first because Snap returns
        // the same text for both (snapd is itself a snap, see
        // UpgradeScriptTests.SnapSelfUpdate_IsTheSameScript...), and "the Snap script" is the more
        // useful of the two labels for one shared entry.
        var isManaged = string.Equals(script, manager.BuildScript(false), StringComparison.Ordinal);
        var isSelfUpdate = string.Equals(script, manager.BuildScript(true), StringComparison.Ordinal);
        if (!isManaged && !isSelfUpdate)
        {
            return false;
        }

        var slug = Slug(manager.Name).ToLowerInvariant();
        identity = isManaged
            ? new ApprovedScriptIdentity($"{manager.Name} (any managed application)", slug, null)
            {
                IsPackageManagerScript = true,
            }
            : new ApprovedScriptIdentity($"{manager.Name} (self-update)", $"{slug}-self-update", null)
            {
                IsPackageManagerScript = true,
            };
        return true;
    }

    /// <summary>
    /// Reduces a name to something safe as a single git path component. Case is preserved, because
    /// an identifier's casing is part of it — winget knows Firefox as <c>Mozilla.Firefox</c>, and a
    /// filename that quietly disagreed would be one more thing to reconcile by hand when reading the
    /// repository.
    /// </summary>
    private static string Slug(string value)
    {
        var slug = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                // One separator per run, so "Visual Studio  Code" doesn't become a name full of
                // empty gaps.
                slug.Append('-');
            }
        }

        // Required to start and end with a letter or digit, which is the one rule that rules out
        // every awkward name at once: a leading dot would make it a hidden file, "." and ".." are
        // not filenames at all, and "..-..-etc-passwd" — what "../../etc/passwd" reduces to
        // otherwise — is safe but unreadable.
        var trimmed = TrimToAlphanumericEnds(slug.ToString());
        if (trimmed.Length > MaxFileBaseNameLength)
        {
            trimmed = TrimToAlphanumericEnds(trimmed[..MaxFileBaseNameLength]);
        }

        return trimmed.Length == 0 ? FallbackFileBaseName : trimmed;
    }

    private static string TrimToAlphanumericEnds(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && !char.IsAsciiLetterOrDigit(value[start]))
        {
            start++;
        }

        while (end > start && !char.IsAsciiLetterOrDigit(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }
}
