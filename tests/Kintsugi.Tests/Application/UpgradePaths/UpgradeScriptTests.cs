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
        foreach (var name in new[] { PackageManagerCatalog.Homebrew, PackageManagerCatalog.Winget, PackageManagerCatalog.Chocolatey })
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
