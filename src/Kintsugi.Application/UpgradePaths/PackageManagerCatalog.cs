namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// The package managers this system knows how to upgrade an application with, without asking the
/// AI anything: their upgrade mechanics are already fully documented, so each gets a fixed,
/// deterministic, server-written script instead (see <see cref="RecognizedPackageManager.BuildScript"/>).
/// An application reported with a <c>packageManager</c> this catalog doesn't list resolves to
/// NotFound with an explanatory note — see
/// <c>ResearchApplicationUpgradePathCommandHandler.ApplyPackageManagerCommandAsync</c>.
/// </summary>
/// <remarks>
/// Recognition is by name, matched case-insensitively against what an agent reports in
/// <c>InstalledApp.package_manager</c> — "Homebrew" from the macOS agent's <c>scan_homebrew</c>,
/// "winget"/"Chocolatey" from the Windows agent's <c>scan_winget</c>/<c>scan_chocolatey</c>. The
/// same names are what <see cref="PlatformBucket.ForPackageManager"/> keys each manager's upgrade
/// rows by, so a name reported with different casing by two hosts must not produce two rows —
/// hence <see cref="Canonicalize"/>.
/// </remarks>
public static class PackageManagerCatalog
{
    public const string Homebrew = "Homebrew";
    public const string Winget = "winget";
    public const string Chocolatey = "Chocolatey";

    private static readonly IReadOnlyDictionary<string, RecognizedPackageManager> ByName =
        new Dictionary<string, RecognizedPackageManager>(StringComparer.OrdinalIgnoreCase)
        {
            [Homebrew] = new(Homebrew, ScriptLanguage.Bash, HomebrewUpgradeScript.Build),
            [Winget] = new(Winget, ScriptLanguage.PowerShell, WingetUpgradeScript.Build),
            [Chocolatey] = new(Chocolatey, ScriptLanguage.PowerShell, ChocolateyUpgradeScript.Build)
        };

    public static bool TryGet(string? name, out RecognizedPackageManager manager)
    {
        if (name is not null && ByName.TryGetValue(name, out var found))
        {
            manager = found;
            return true;
        }

        manager = default!;
        return false;
    }

    /// <summary>
    /// The catalog's own casing for <paramref name="name"/>, or <paramref name="name"/> unchanged
    /// when it isn't a recognized manager. Only the recognized set can be normalized — an
    /// unrecognized manager has no canonical form to normalize toward, and two hosts spelling one
    /// differently simply get separate (equally unresolvable) rows.
    /// </summary>
    public static string Canonicalize(string name) =>
        TryGet(name, out var manager) ? manager.Name : name;
}

/// <param name="BuildScript">Takes <c>isSelfUpdate</c> — true for the manager's own row (upgrading
/// the manager itself), false for one of the applications it manages. Returns content that is
/// byte-identical for every application in that case, so one human "Sign Script" review covers
/// them all (see <c>IUpgradePathRepository.FindExistingSignatureForScriptAsync</c>).</param>
public record RecognizedPackageManager(string Name, ScriptLanguage Language, Func<bool, string> BuildScript);
