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
/// <c>InstalledApp.package_manager</c> — "Homebrew" and "App Store" from the macOS agent's
/// <c>scan_homebrew</c> and <c>read_app_bundle</c>, "winget"/"Chocolatey" from the Windows agent's
/// <c>scan_winget</c>/<c>scan_chocolatey</c>, "Flatpak"/"Snap" from the Linux agent's
/// <c>scan_flatpak</c>/<c>scan_snap</c>. The same names are
/// what <see cref="PlatformBucket.ForPackageManager"/> keys each manager's upgrade rows by, so a
/// name reported with different casing by two hosts must not produce two rows — hence
/// <see cref="Canonicalize"/>.
/// </para>
/// <para>
/// There is a hard entry requirement for this catalog, and it is not "the agent can drive it".
/// A manager belongs here only if its catalog can be queried <em>over HTTP from the API server</em>,
/// because that is where a script's <c>--update-version</c> mode runs (see
/// <c>IUpgradePathResearchClient.CheckScriptVersionAsync</c>) and because one row per (application,
/// manager) is shared by the whole fleet. Homebrew, winget, Chocolatey, Flathub, the Snap Store and
/// the Mac App Store (through Apple's iTunes Search API) all publish one global catalog and satisfy
/// both. A distribution's own package manager satisfies
/// neither — "the latest version of curl" depends on which repositories <em>that</em> host has
/// configured, and asking on the API server would confidently return the API server's answer — so
/// apt/dnf/zypper/pacman are deliberately absent, and the Linux agent reports what they manage as
/// OS updates rather than as applications. See its <c>os_update</c> module.
/// </para>
/// </remarks>
public static class PackageManagerCatalog
{
    public const string Homebrew = "Homebrew";
    public const string AppStore = "App Store";
    public const string Winget = "winget";
    public const string Chocolatey = "Chocolatey";
    public const string Flatpak = "Flatpak";
    public const string Snap = "Snap";

    private static readonly IReadOnlyDictionary<string, RecognizedPackageManager> ByName =
        new Dictionary<string, RecognizedPackageManager>(StringComparer.OrdinalIgnoreCase)
        {
            [Homebrew] = new(Homebrew, ScriptLanguage.Bash, HomebrewUpgradeScript.Build),
            [AppStore] = new(AppStore, ScriptLanguage.Bash, AppStoreUpgradeScript.Build),
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
    /// The script this server's current build would write for a row on <paramref name="platform"/>,
    /// or null when that bucket is not a recognized package manager's — an AI-researched script has no
    /// canonical current version to compare against.
    /// </summary>
    /// <remarks>
    /// Exists so a row's stored script can be compared against what this build would produce, which
    /// is the only way to notice that an edit to a <c>*UpgradeScript.Build</c> body has left a
    /// signed row running an older text. Nothing rewrites a signed row on the strength of it — see
    /// <c>TakeServerWrittenScriptCommand</c>, which a human presses, and
    /// <c>RegisterApplicationsCommandHandler</c>, which deliberately does not.
    /// </remarks>
    /// <param name="applicationName">Accepted for the caller's convenience and not consulted: every
    /// row in a manager's bucket, the manager's own included, gets the same text, and the script tells
    /// the manager's own row apart at runtime by <c>--appName</c> (see
    /// <see cref="RecognizedPackageManager.BuildScript"/>). It used to select a second, self-update
    /// text for the row named after the manager.</param>
    public static string? CurrentScriptFor(string applicationName, string platform)
    {
        _ = applicationName;
        var managerName = PlatformBucket.PackageManagerNameFrom(platform);
        return managerName is not null && TryGet(managerName, out var manager) ? manager.BuildScript() : null;
    }
}

/// <param name="BuildScript">Returns content that is byte-identical for every row in the manager's
/// bucket — every application it manages <em>and its own row</em>, since a manager is trivially its
/// own manager — so one human "Sign Script" review covers them all (see
/// <c>IUpgradePathRepository.FindExistingSignatureForScriptAsync</c>), and so the Applications
/// screen can show the manager's script once, on the manager's row, on behalf of the applications
/// nested under it. Where the manager's own row needs different handling (Homebrew is not a
/// formula, winget is not a winget package under its own name, Flatpak is a distribution package) the
/// script branches at <em>runtime</em> on <c>--appName</c> being the manager's name — the same name
/// the agent reports the manager under and <c>PrepareUpgradePathScanQueryHandler</c> recognizes its
/// row by. See the remarks on <see cref="HomebrewUpgradeScript"/> for why one text rather than two.</param>
public record RecognizedPackageManager(string Name, ScriptLanguage Language, Func<string> BuildScript);
