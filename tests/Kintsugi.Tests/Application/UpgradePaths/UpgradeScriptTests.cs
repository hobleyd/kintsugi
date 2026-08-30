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
    public static TheoryData<string> AllScripts() => new()
    {
        HomebrewUpgradeScript.Build(isSelfUpdate: false),
        HomebrewUpgradeScript.Build(isSelfUpdate: true),
        WingetUpgradeScript.Build(isSelfUpdate: false),
        WingetUpgradeScript.Build(isSelfUpdate: true),
        ChocolateyUpgradeScript.Build(isSelfUpdate: false),
        ChocolateyUpgradeScript.Build(isSelfUpdate: true),
        FlatpakUpgradeScript.Build(isSelfUpdate: false),
        FlatpakUpgradeScript.Build(isSelfUpdate: true),
        // Snap appears once, not twice: both cases return the same string (see
        // SnapSelfUpdate_IsTheSameScript_BecauseSnapdIsItselfASnap), and a duplicate row here would
        // be two theory cases with the same display name asserting the same thing.
        SnapUpgradeScript.Build(isSelfUpdate: false),
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
        // runtime. That's what makes every row for a given (manager, isSelfUpdate) byte-identical,
        // so one "Sign Script" review covers them all
        // (see IUpgradePathRepository.FindExistingSignatureForScriptAsync).
        Assert.DoesNotContain("Firefox", script);
        Assert.DoesNotContain("Mozilla", script);
    }

    [Fact]
    public void HomebrewScript_IsBash()
    {
        Assert.StartsWith("#!/bin/bash", HomebrewUpgradeScript.Build(isSelfUpdate: false));
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
        FlatpakUpgradeScript.Build(isSelfUpdate: false),
        FlatpakUpgradeScript.Build(isSelfUpdate: true),
        SnapUpgradeScript.Build(isSelfUpdate: false),
    };

    [Fact]
    public void FlatpakScript_ChecksFlathubOverHttp_SoTheVersionCheckCanRunOnTheApiServer()
    {
        var script = FlatpakUpgradeScript.Build(isSelfUpdate: false);

        // The catalog's entry requirement, asserted at the source: --update-version runs on the API
        // server, so it may only reach the network. A `flatpak remote-info` here would answer about
        // the server rather than the managed host.
        Assert.Contains("flathub.org/api/v2/appstream/", script);
        Assert.DoesNotContain("remote-info", script);
    }

    [Fact]
    public void FlatpakScript_UpdatesTheSystemInstallation_NonInteractively()
    {
        var script = FlatpakUpgradeScript.Build(isSelfUpdate: false);

        // --user installations belong to one person's home directory and this runs as root; the
        // agent never reports one (see system_info::scan_flatpak), so this must never try.
        Assert.Contains("flatpak update --system", script);
        Assert.Contains("--noninteractive", script);
        Assert.DoesNotContain("flatpak update --user", script);
    }

    [Fact]
    public void FlatpakSelfUpdate_GoesThroughTheDistributionsPackageManager_AndDoesNotAssumeApt()
    {
        var script = FlatpakUpgradeScript.Build(isSelfUpdate: true);

        // Flatpak is not itself a Flatpak — it ships as a distribution package, so its own row has
        // to work on whichever distribution the host runs.
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
    public void FlatpakSelfUpdate_DoesNotClaimAnUpstreamVersion_ItCannotActuallyInstall()
    {
        var script = FlatpakUpgradeScript.Build(isSelfUpdate: true);

        Assert.DoesNotContain("github.com/flatpak/flatpak/releases", script);
        Assert.DoesNotContain("%{redirect_url}", script);
    }

    [Fact]
    public void SnapScript_ChecksTheSnapStoreOverHttp_WithTheHeaderItRequires()
    {
        var script = SnapUpgradeScript.Build(isSelfUpdate: false);

        Assert.Contains("api.snapcraft.io/v2/snaps/info/", script);
        // The store rejects the request outright without this header, which would make every snap's
        // version check fail and leave LatestVersion null — and a null LatestVersion means nothing
        // on that platform ever patches.
        Assert.Contains("Snap-Device-Series: 16", script);
    }

    [Fact]
    public void SnapScript_PutsSnapBinOnThePath_BecauseASystemdServiceDoesNotHaveIt()
    {
        var script = SnapUpgradeScript.Build(isSelfUpdate: false);

        Assert.Contains("/snap/bin", script);
        Assert.Contains("snap refresh", script);
    }

    /// <summary>
    /// The deliberate exception to <see cref="SelfUpdateScripts_DifferFromManagedOnes"/>. Homebrew
    /// is not a formula and Flatpak is not a Flatpak, so each needs a different script for its own
    /// row — but snapd genuinely is a snap, published under the name "snapd", so its self-update is
    /// `snap refresh` with a different --appId and nothing more. The two rows sharing one script
    /// means they share one signature, and one human review covers both.
    /// </summary>
    [Fact]
    public void SnapSelfUpdate_IsTheSameScript_BecauseSnapdIsItselfASnap()
    {
        Assert.Equal(SnapUpgradeScript.Build(isSelfUpdate: true), SnapUpgradeScript.Build(isSelfUpdate: false));
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
        WingetUpgradeScript.Build(isSelfUpdate: false),
        WingetUpgradeScript.Build(isSelfUpdate: true),
        ChocolateyUpgradeScript.Build(isSelfUpdate: false),
        ChocolateyUpgradeScript.Build(isSelfUpdate: true),
    };

    [Fact]
    public void WingetScript_UpgradesByExactId_SoAPartialMatchCannotUpgradeADifferentPackage()
    {
        var script = WingetUpgradeScript.Build(isSelfUpdate: false);

        Assert.Contains("winget upgrade --exact --id $AppId", script);
        // Every flag winget needs to run unattended — without them it blocks on a prompt no one
        // will ever see, and the patch cycle just hangs.
        Assert.Contains("--silent", script);
        Assert.Contains("--accept-package-agreements", script);
        Assert.Contains("--accept-source-agreements", script);
        Assert.Contains("--disable-interactivity", script);
    }

    [Fact]
    public void ChocolateyScript_UpgradesUnattended()
    {
        var script = ChocolateyUpgradeScript.Build(isSelfUpdate: false);

        Assert.Contains("choco upgrade $AppId", script);
        Assert.Contains("-y", script);
    }

    [Fact]
    public void SelfUpdateScripts_DifferFromManagedOnes()
    {
        // They're stored as two separate rows under the same bucket and signed independently; if
        // the builder ever returned the same text for both, signing one would silently sign the
        // other.
        Assert.NotEqual(HomebrewUpgradeScript.Build(true), HomebrewUpgradeScript.Build(false));
        Assert.NotEqual(WingetUpgradeScript.Build(true), WingetUpgradeScript.Build(false));
        Assert.NotEqual(ChocolateyUpgradeScript.Build(true), ChocolateyUpgradeScript.Build(false));
        Assert.NotEqual(FlatpakUpgradeScript.Build(true), FlatpakUpgradeScript.Build(false));
        // Snap is the deliberate exception — see SnapSelfUpdate_IsTheSameScript_BecauseSnapdIsItselfASnap.
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
