using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Tests.Application.UpgradePaths;

/// <summary>
/// Covers the server-written upgrade scripts and the language mapping that decides which
/// interpreter validates and runs them. The scripts themselves aren't executed here — that would
/// need a live network and a real winget/choco — so what's asserted is the contract every consumer
/// depends on: the CLI shape, the absence of any baked-in package name (which is what lets one
/// human signature cover every application), and that a bash script never reaches a Windows bucket.
/// </summary>
public class UpgradeScriptTests
{
    /// <summary>One text per manager — the manager's own row included — so one entry each.</summary>
    public static TheoryData<string> AllScripts() => new()
    {
        HomebrewUpgradeScript.Build(),
        WingetUpgradeScript.Build(),
        ChocolateyUpgradeScript.Build(),
        FlatpakUpgradeScript.Build(),
        SnapUpgradeScript.Build(),
    };

    [Theory]
    [MemberData(nameof(AllScripts))]
    public void EveryServerWrittenScript_ImplementsTheFullCliContract(string script)
    {
        // The same four tokens AiUpgradePathResearchClient.ValidateScriptAsync requires of an
        // AI-authored script — the agent and the server-side version check invoke both kinds
        // identically, so a server-written one has no licence to differ.
        Assert.Contains("--appName", script);
        Assert.Contains("--appId", script);
        Assert.Contains("--update-version", script);
        Assert.Contains("--update", script);
    }

    [Theory]
    [MemberData(nameof(AllScripts))]
    public void EveryServerWrittenScript_IsIdenticalAcrossApplications(string script)
    {
        // No application name or id is ever baked in — each is read from --appName/--appId at
        // runtime. That's what makes every row of a manager byte-identical, its own row included,
        // so one "Sign Script" review covers them all
        // (see IUpgradePathRepository.FindExistingSignatureForScriptAsync).
        Assert.DoesNotContain("Firefox", script);
        Assert.DoesNotContain("Mozilla", script);
    }

    [Fact]
    public void HomebrewScript_IsBash()
    {
        Assert.StartsWith("#!/bin/bash", HomebrewUpgradeScript.Build());
    }

    [Fact]
    public void HomebrewScript_LooksTheNameUpInLowercaseToo_BecauseTheBrewApiIsCaseSensitive()
    {
        // formulae.brew.sh/api/{formula,cask}/<token>.json is case-sensitive and every brew token is
        // lowercase, but a row's name doesn't have to be: PrepareUpgradePathScanQueryHandler groups
        // an application's variants case-insensitively, so one can settle on the display-cased
        // /Applications bundle name ("Nextcloud") instead of `brew list`'s token ("nextcloud"). Both
        // URLs then 404, LatestVersion stays null, updateAvailable is false and is_patchable is
        // false — the application silently never patches. Asserted here because the failure is
        // invisible: a null LatestVersion is indistinguishable from "no update available".
        var script = HomebrewUpgradeScript.Build();

        Assert.Contains("tr '[:upper:]' '[:lower:]'", script);
        // The name as given is still tried first, so a row already named by its own token is never
        // transformed on the way to a lookup that already worked.
        Assert.Contains("for candidate in \"$APP_NAME\"", script);
    }

    /// <summary>
    /// Homebrew is not a formula, so its own row used to get a second script. It no longer does: the
    /// Applications screen shows the manager's script once, on the "Homebrew" row, on behalf of every
    /// formula and cask nested under it, and that is only honest if the manager's bytes are its
    /// children's bytes. So one script serves every row and tells Homebrew's own apart at runtime by
    /// <c>--appName</c>, which is also what lets one signature cover Homebrew and everything it
    /// manages. See the remarks on <see cref="HomebrewUpgradeScript"/>.
    /// </summary>
    [Fact]
    public void HomebrewScript_TellsItsOwnRowApartAtRuntime_ByTheNameTheAgentReports()
    {
        var script = HomebrewUpgradeScript.Build();

        Assert.Equal(HomebrewUpgradeScript.Build(), script);

        // The branch is on the name the macOS agent reports Homebrew itself under
        // (system_info::HOMEBREW_NAME), compared case-insensitively because rows are matched that
        // way throughout (see UpgradePathRepository.GetAsync).
        Assert.Contains("is_homebrew_itself()", script);
        Assert.Contains("= \"homebrew\"", script);
        // Its version comes from GitHub's releases redirect — the formula API does not know it —
        // and, as CLAUDE.md records, with -fsS rather than -fsSL or the redirect is followed and the
        // variable reporting it is empty.
        Assert.Contains("https://github.com/Homebrew/brew/releases/latest", script);
        Assert.Contains("curl -fsS -o /dev/null -w '%{redirect_url}'", script);
        Assert.DoesNotContain("curl -fsSL -o /dev/null", script);
    }

    [Fact]
    public void HomebrewScript_UpgradesHomebrewItselfOnEveryUpdate_AndOnlyThatOnItsOwnRow()
    {
        var script = HomebrewUpgradeScript.Build();

        // `brew update` is Homebrew's own self-update as well as the index refresh, and it runs
        // unconditionally before the per-application upgrade — so every application upgrade brings
        // Homebrew along, and Homebrew's row needs nothing more.
        var update = script.IndexOf("\nbrew update\n", StringComparison.Ordinal);
        var upgrade = script.IndexOf("brew upgrade \"$APP_NAME\"", StringComparison.Ordinal);
        Assert.True(update >= 0, "brew update must run on its own line");
        Assert.True(upgrade > update, "brew update must precede the per-application upgrade");

        // Never a blanket `brew upgrade`: that would patch every formula on the host from the
        // manager's row, approved or not. Each application has a row of its own for that.
        Assert.DoesNotContain("brew upgrade\n", script);
        Assert.DoesNotContain("brew update && brew upgrade\n", script);
    }

    [Theory]
    [MemberData(nameof(LinuxScripts))]
    public void LinuxScripts_AreBash(string script)
    {
        Assert.StartsWith("#!/bin/bash", script);
        Assert.DoesNotContain("Invoke-RestMethod", script);
    }

    public static TheoryData<string> LinuxScripts() => new()
    {
        FlatpakUpgradeScript.Build(),
        SnapUpgradeScript.Build(),
    };

    [Fact]
    public void FlatpakScript_ChecksFlathubOverHttp_SoTheVersionCheckCanRunOnTheApiServer()
    {
        var script = FlatpakUpgradeScript.Build();

        // The catalog's entry requirement, asserted at the source: --update-version runs on the API
        // server, so it may only reach the network. A `flatpak remote-info` here would answer about
        // the server rather than the managed host.
        Assert.Contains("flathub.org/api/v2/appstream/", script);
        Assert.DoesNotContain("remote-info", script);
    }

    [Fact]
    public void FlatpakScript_UpdatesTheSystemInstallation_NonInteractively()
    {
        var script = FlatpakUpgradeScript.Build();

        // --user installations belong to one person's home directory and this runs as root; the
        // agent never reports one (see system_info::scan_flatpak), so this must never try.
        Assert.Contains("flatpak update --system", script);
        Assert.Contains("--noninteractive", script);
        Assert.DoesNotContain("flatpak update --user", script);
    }

    /// <summary>
    /// Flatpak is not a Flatpak — it ships as a distribution package — so its own row needs different
    /// handling from every application it manages. One script serves both (see
    /// <see cref="RecognizedPackageManager.BuildScript"/>) and tells Flatpak's row apart at runtime by
    /// <c>--appName</c>, the name the Linux agent reports it under (<c>system_info::FLATPAK_NAME</c>).
    /// </summary>
    [Fact]
    public void FlatpakScript_TellsItsOwnRowApartAtRuntime_ByTheNameTheAgentReports()
    {
        var script = FlatpakUpgradeScript.Build();

        Assert.Contains("is_flatpak_itself()", script);
        Assert.Contains("= \"flatpak\"", script);
        // --appName has to be read for that, where it used to be discarded.
        Assert.Contains("--appName) APP_NAME=\"$2\"", script);
    }

    [Fact]
    public void FlatpakScript_UpgradesFlatpakItselfThroughTheDistributionsPackageManager_AndDoesNotAssumeApt()
    {
        var script = FlatpakUpgradeScript.Build();

        // Flatpak's own row has to work on whichever distribution the host runs.
        foreach (var manager in new[] { "apt-get", "dnf", "zypper", "pacman", "apk" })
        {
            Assert.Contains(manager, script);
        }
        Assert.Contains("DEBIAN_FRONTEND=noninteractive", script);
    }

    /// <summary>
    /// Flatpak's own row must not compare an upstream release against a distribution package.
    /// github.com/flatpak/flatpak publishes 1.18.x while Debian 12 ships 1.14.x, so a row sourcing
    /// its version upstream would read "update available" permanently, and its --update would exit 0
    /// every cycle having changed nothing — a patch that always succeeds and never does anything,
    /// which nothing downstream can tell apart from one that works. Declining to report a version
    /// leaves LatestVersion null, which makes updateAvailable false, which makes the agent skip it;
    /// flatpak is then patched by the OS-update path like any other distribution package.
    /// </summary>
    [Fact]
    public void FlatpakScript_DoesNotClaimAnUpstreamVersionForFlatpakItself_ItCannotActuallyInstall()
    {
        var script = FlatpakUpgradeScript.Build();

        Assert.DoesNotContain("github.com/flatpak/flatpak/releases", script);
        Assert.DoesNotContain("%{redirect_url}", script);
    }

    [Fact]
    public void SnapScript_ChecksTheSnapStoreOverHttp_WithTheHeaderItRequires()
    {
        var script = SnapUpgradeScript.Build();

        Assert.Contains("api.snapcraft.io/v2/snaps/info/", script);
        // The store rejects the request outright without this header, which would make every snap's
        // version check fail and leave LatestVersion null — and a null LatestVersion means nothing
        // on that platform ever patches.
        Assert.Contains("Snap-Device-Series: 16", script);
    }

    [Fact]
    public void SnapScript_PutsSnapBinOnThePath_BecauseASystemdServiceDoesNotHaveIt()
    {
        var script = SnapUpgradeScript.Build();

        Assert.Contains("/snap/bin", script);
        Assert.Contains("snap refresh", script);
    }

    /// <summary>
    /// snapd genuinely is a snap, published under the name "snapd" — the id the Linux agent reports
    /// its own row with — so its refresh is `snap refresh` with a different --appId and nothing more.
    /// Where Homebrew, winget and Flatpak branch at runtime to serve their own row from one text, Snap
    /// and Chocolatey have nothing to branch on.
    /// </summary>
    [Fact]
    public void SnapScript_NeedsNoBranchForSnapdsOwnRow_BecauseSnapdIsItselfASnap()
    {
        var script = SnapUpgradeScript.Build();

        Assert.DoesNotContain("snap refresh snapd", script);
        Assert.DoesNotContain("_itself", script);
    }

    [Theory]
    [InlineData(PackageManagerCatalog.Homebrew, "Homebrew", "firefox")]
    [InlineData(PackageManagerCatalog.Winget, "winget", "Mozilla.Firefox")]
    [InlineData(PackageManagerCatalog.Chocolatey, "Chocolatey", "firefox")]
    [InlineData(PackageManagerCatalog.Flatpak, "Flatpak", "org.mozilla.firefox")]
    [InlineData(PackageManagerCatalog.Snap, "Snap", "firefox")]
    public void CurrentScriptFor_TheManagersOwnRow_IsTheSameTextAsAManagedRows(string managerName, string ownRow, string managedRow)
    {
        // How the Upgrade Scripts page notices that a signed row holds a script this build no longer
        // writes — and why a manager's own row and its applications collapse into one entry there.
        // The two share a bucket (a manager is its own manager) and used to be told apart by name;
        // now the *script* does that at runtime and the server writes one text for both, whatever the
        // casing of the row's name.
        Assert.True(PackageManagerCatalog.TryGet(managerName, out var manager));
        var bucket = PlatformBucket.ForPackageManager(managerName);

        Assert.Equal(manager.BuildScript(), PackageManagerCatalog.CurrentScriptFor(managedRow, bucket));
        Assert.Equal(manager.BuildScript(), PackageManagerCatalog.CurrentScriptFor(ownRow, bucket));
        Assert.Equal(manager.BuildScript(), PackageManagerCatalog.CurrentScriptFor(ownRow.ToUpperInvariant(), bucket));
    }

    [Theory]
    [InlineData(PlatformBucket.MacOs)]
    [InlineData(PlatformBucket.Windows)]
    [InlineData(PlatformBucket.Linux)]
    [InlineData(PlatformBucket.Generic)]
    [InlineData("pm:APT")]
    public void CurrentScriptFor_ARowThisServerWritesNoScriptFor_IsNull(string platform)
    {
        // An AI-researched script has no canonical current version to differ from, and neither does
        // an unrecognized manager's row — apt and friends are deliberately absent from the catalog.
        Assert.Null(PackageManagerCatalog.CurrentScriptFor("Firefox", platform));
    }

    [Theory]
    [MemberData(nameof(WindowsScripts))]
    public void WindowsScripts_AreNeverBash(string script)
    {
        // A bash script reaching a Windows host is precisely the failure the per-manager platform
        // bucket exists to prevent; asserting it at the source too means a copy-paste from the
        // Homebrew builder can't reintroduce it silently.
        Assert.DoesNotContain("#!/bin/bash", script);
        Assert.Contains("Set-StrictMode", script);
    }

    public static TheoryData<string> WindowsScripts() => new()
    {
        WingetUpgradeScript.Build(),
        ChocolateyUpgradeScript.Build(),
    };

    [Fact]
    public void WingetScript_UpgradesByExactId_SoAPartialMatchCannotUpgradeADifferentPackage()
    {
        var script = WingetUpgradeScript.Build();

        Assert.Contains("winget upgrade --exact --id $upgradeId", script);
        // Every flag winget needs to run unattended — without them it blocks on a prompt no one
        // will ever see, and the patch cycle just hangs.
        Assert.Contains("--silent", script);
        Assert.Contains("--accept-package-agreements", script);
        Assert.Contains("--accept-source-agreements", script);
        Assert.Contains("--disable-interactivity", script);
    }

    /// <summary>
    /// winget is not a winget package under its own name — it ships inside App Installer — so its
    /// own row needs a different version source and a different id to upgrade. One script serves it
    /// and every package it manages (see <see cref="RecognizedPackageManager.BuildScript"/>), told
    /// apart at runtime by <c>--appName</c>, the name the Windows agent reports winget's row under
    /// (<c>system_info::WINGET_NAME</c>).
    /// </summary>
    [Fact]
    public void WingetScript_TellsItsOwnRowApartAtRuntime_ByTheNameTheAgentReports()
    {
        var script = WingetUpgradeScript.Build();

        Assert.Contains("function Test-IsWingetItself", script);
        Assert.Contains("$AppName -ieq 'winget'", script);
        Assert.Contains("https://github.com/microsoft/winget-cli/releases/latest", script);
        Assert.Contains("if (Test-IsWingetItself) { 'Microsoft.AppInstaller' } else { $AppId }", script);
        // Still one lookup path for every managed package: the manifest tree of winget-pkgs.
        Assert.Contains("repos/microsoft/winget-pkgs/contents", script);
    }

    [Fact]
    public void ChocolateyScript_UpgradesUnattended_AndAddressesChocolateyItselfByTheSameId()
    {
        var script = ChocolateyUpgradeScript.Build();

        Assert.Contains("choco upgrade $AppId", script);
        Assert.Contains("-y", script);
        // `chocolatey` is an ordinary package on the same feed and the Windows agent reports the
        // manager's own row with that id (system_info::scan_chocolatey), so nothing is hard-coded
        // for it any more — the same script with a different --appId is the whole self-update.
        Assert.DoesNotContain("'chocolatey'", script);
        Assert.DoesNotContain("_itself", script);
    }

    /// <summary>
    /// A regression guard for a bug that shipped and stayed invisible: `%{redirect_url}` reports the
    /// redirect curl did <em>not</em> follow, so combining it with `-L` makes curl follow the
    /// redirect and report an empty string. Every script using the pair returned no version at all,
    /// which meant a null <c>LatestVersion</c>, which meant <c>updateAvailable</c> false, which meant
    /// the agent's <c>is_patchable</c> said no — the row simply never patched, and nothing anywhere
    /// reported an error. Nothing about that failure is visible without running the script.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllScripts))]
    public void NoScript_CombinesTheRedirectUrlTrickWithFollowRedirects(string script)
    {
        // Vacuous for the two PowerShell scripts by design — they express the same trick as
        // `Invoke-WebRequest -MaximumRedirection 0` plus the Location header, which cannot follow
        // the redirect and so cannot have this bug. The theory covers every script anyway so a new
        // bash one is caught the moment it is added to AllScripts.
        var invocations = script
            .Split('\n')
            // Comments are skipped, not because they don't matter but because the ones next to
            // these calls exist precisely to name the flag that must not appear.
            .Where(line => !line.TrimStart().StartsWith('#'))
            .Where(line => line.Contains("%{redirect_url}"));

        foreach (var line in invocations)
        {
            Assert.DoesNotContain("-fsSL", line);
            Assert.DoesNotContain("--location", line);
        }
    }

    [Theory]
    [InlineData(PlatformBucket.MacOs, ScriptLanguage.Bash)]
    [InlineData(PlatformBucket.Linux, ScriptLanguage.Bash)]
    [InlineData(PlatformBucket.Generic, ScriptLanguage.Bash)]
    [InlineData(PlatformBucket.Windows, ScriptLanguage.PowerShell)]
    public void ScriptLanguages_For_MapsAnOsBucketToItsInterpreter(string platform, ScriptLanguage expected)
    {
        Assert.Equal(expected, ScriptLanguages.For(platform));
    }

    [Theory]
    [InlineData(PackageManagerCatalog.Homebrew, ScriptLanguage.Bash)]
    [InlineData(PackageManagerCatalog.Winget, ScriptLanguage.PowerShell)]
    [InlineData(PackageManagerCatalog.Chocolatey, ScriptLanguage.PowerShell)]
    [InlineData(PackageManagerCatalog.Flatpak, ScriptLanguage.Bash)]
    [InlineData(PackageManagerCatalog.Snap, ScriptLanguage.Bash)]
    public void ScriptLanguages_For_MapsAPackageManagerBucketToItsInterpreter(string manager, ScriptLanguage expected)
    {
        Assert.Equal(expected, ScriptLanguages.For(PlatformBucket.ForPackageManager(manager)));
    }

    [Fact]
    public void ScriptLanguages_MatchTheCatalogsOwnDeclaredLanguage()
    {
        // The catalog decides which builder writes a manager's script; ScriptLanguages decides which
        // interpreter validates and runs it. These agreeing is not optional — a mismatch means every
        // version check for that manager fails, LatestVersion stays null, and nothing ever patches.
        foreach (var name in new[]
                 {
                     PackageManagerCatalog.Homebrew, PackageManagerCatalog.Winget, PackageManagerCatalog.Chocolatey,
                     PackageManagerCatalog.Flatpak, PackageManagerCatalog.Snap
                 })
        {
            Assert.True(PackageManagerCatalog.TryGet(name, out var manager));
            Assert.Equal(manager.Language, ScriptLanguages.For(PlatformBucket.ForPackageManager(name)));
        }
    }

    [Fact]
    public void ScriptLanguages_Interpreter_AndExtension_Pair()
    {
        // pwsh -File refuses a file that isn't .ps1, so these two must move together.
        Assert.Equal("pwsh", ScriptLanguage.PowerShell.Interpreter());
        Assert.Equal(".ps1", ScriptLanguage.PowerShell.FileExtension());
        Assert.Equal("bash", ScriptLanguage.Bash.Interpreter());
        Assert.Equal(".sh", ScriptLanguage.Bash.FileExtension());
    }

    [Fact]
    public void PackageManagerCatalog_DoesNotRecognizeAnUnknownManager()
    {
        Assert.False(PackageManagerCatalog.TryGet("SomeNewManager", out _));
        Assert.False(PackageManagerCatalog.TryGet(null, out _));
    }
}
