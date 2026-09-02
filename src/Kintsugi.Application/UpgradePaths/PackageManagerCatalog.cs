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
/// <para>
/// Recognition is by name, matched case-insensitively against what an agent reports in
/// <c>InstalledApp.package_manager</c> — "Homebrew" from the macOS agent's <c>scan_homebrew</c>,
/// "winget"/"Chocolatey" from the Windows agent's <c>scan_winget</c>/<c>scan_chocolatey</c>,
/// "Flatpak"/"Snap" from the Linux agent's <c>scan_flatpak</c>/<c>scan_snap</c>. The same names are
/// what <see cref="PlatformBucket.ForPackageManager"/> keys each manager's upgrade rows by, so a
/// name reported with different casing by two hosts must not produce two rows — hence
/// <see cref="Canonicalize"/>.
/// </para>
/// <para>
/// There is a hard entry requirement for this catalog, and it is not "the agent can drive it".
/// A manager belongs here only if its catalog can be queried <em>over HTTP from the API server</em>,
/// because that is where a script's <c>--update-version</c> mode runs (see
/// <c>IUpgradePathResearchClient.CheckScriptVersionAsync</c>) and because one row per (application,
/// manager) is shared by the whole fleet. Homebrew, winget, Chocolatey, Flathub and the Snap Store
/// all publish one global catalog and satisfy both. A distribution's own package manager satisfies
/// neither — "the latest version of curl" depends on which repositories <em>that</em> host has
/// configured, and asking on the API server would confidently return the API server's answer — so
/// apt/dnf/zypper/pacman are deliberately absent, and the Linux agent reports what they manage as
/// OS updates rather than as applications. See its <c>os_update</c> module.
/// </para>
/// </remarks>
public static class PackageManagerCatalog
{
    public const string Homebrew = "Homebrew";
    public const string Winget = "winget";
    public const string Chocolatey = "Chocolatey";
    public const string Flatpak = "Flatpak";
    public const string Snap = "Snap";

    private static readonly IReadOnlyDictionary<string, RecognizedPackageManager> ByName =
        new Dictionary<string, RecognizedPackageManager>(StringComparer.OrdinalIgnoreCase)
        {
            [Homebrew] = new(Homebrew, ScriptLanguage.Bash, HomebrewUpgradeScript.Build),
            [Winget] = new(Winget, ScriptLanguage.PowerShell, WingetUpgradeScript.Build),
            [Chocolatey] = new(Chocolatey, ScriptLanguage.PowerShell, ChocolateyUpgradeScript.Build),
            [Flatpak] = new(Flatpak, ScriptLanguage.Bash, FlatpakUpgradeScript.Build),
            [Snap] = new(Snap, ScriptLanguage.Bash, SnapUpgradeScript.Build)
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

    /// <summary>
    /// The script this server's current build would write for the row
    /// (<paramref name="applicationName"/>, <paramref name="platform"/>), or null when that row is
    /// not a recognized package manager's — an AI-researched script has no canonical current
    /// version to compare against.
    /// </summary>
    /// <remarks>
    /// Exists so a row's stored script can be compared against what this build would produce, which
    /// is the only way to notice that an edit to a <c>*UpgradeScript.Build</c> body has left a
    /// signed row running an older text. Nothing rewrites a signed row on the strength of it — see
    /// <c>TakeServerWrittenScriptCommand</c>, which a human presses, and
    /// <c>RegisterApplicationsCommandHandler</c>, which deliberately does not.
    /// </remarks>
    public static string? CurrentScriptFor(string applicationName, string platform)
    {
        var managerName = PlatformBucket.PackageManagerNameFrom(platform);
        if (managerName is null || !TryGet(managerName, out var manager))
        {
            return null;
        }

        // The self-update row is the one named after the manager itself: a manager is its own
        // manager, so its own row lives in the very bucket its managed applications do. Same rule
        // PrepareUpgradePathScanQueryHandler uses to decide which kind of work item to emit.
        var isSelfUpdate = string.Equals(applicationName, manager.Name, StringComparison.OrdinalIgnoreCase);
        return manager.BuildScript(isSelfUpdate);
    }
}

/// <param name="BuildScript">Takes <c>isSelfUpdate</c> — true for the manager's own row (upgrading
/// the manager itself), false for one of the applications it manages. Returns content that is
/// byte-identical for every application in that case, so one human "Sign Script" review covers
/// them all (see <c>IUpgradePathRepository.FindExistingSignatureForScriptAsync</c>).</param>
public record RecognizedPackageManager(string Name, ScriptLanguage Language, Func<bool, string> BuildScript);
