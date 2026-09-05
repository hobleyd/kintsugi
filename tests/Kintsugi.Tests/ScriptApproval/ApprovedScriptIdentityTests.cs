using Kintsugi.Application.ScriptApproval;
using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Tests.ScriptApproval;

/// <summary>
/// What an approval entry publishes itself as. The distinction under test is between a script that
/// belongs to one application and one that a package manager's applications all share — the second
/// kind used to be published under the name of whichever application a reviewer happened to sign it
/// against, which reads as though the review were specific to that application when it is not.
/// </summary>
public class ApprovedScriptIdentityTests
{
    private static readonly string HomebrewBucket = PlatformBucket.ForPackageManager(PackageManagerCatalog.Homebrew);
    private static readonly string WingetBucket = PlatformBucket.ForPackageManager(PackageManagerCatalog.Winget);

    [Fact]
    public void For_AManagedPackageManagerScript_NamesTheManager_NotTheApplicationSigned()
    {
        var identity = ApprovedScriptIdentity.For(
            HomebrewBucket, HomebrewUpgradeScript.Build(), "ada-url", "ada-url");

        Assert.True(identity.IsPackageManagerScript);
        Assert.DoesNotContain("ada-url", identity.DisplayName);
        Assert.Equal("homebrew", identity.FileBaseName);
        // Dropped rather than carried across: the identifier of whichever application the reviewer
        // was looking at says nothing about a script shared by all of them, and
        // UpgradePath.AdoptApprovedScript would copy it onto an unrelated row.
        Assert.Null(identity.ApplicationIdentifier);
    }

    [Theory]
    [InlineData(PackageManagerCatalog.Homebrew, "Homebrew", "brew")]
    [InlineData(PackageManagerCatalog.Winget, "winget", "winget")]
    [InlineData(PackageManagerCatalog.Chocolatey, "Chocolatey", "chocolatey")]
    [InlineData(PackageManagerCatalog.Flatpak, "Flatpak", "flatpak")]
    [InlineData(PackageManagerCatalog.Snap, "Snap", "snapd")]
    public void For_TheManagersOwnRow_IsTheSameEntryAsItsManagedApplications(string managerName, string ownRowName, string ownRowId)
    {
        // One text per manager, the manager's own row included (RecognizedPackageManager.BuildScript),
        // so an entry signed from the manager's row and one signed from a managed application's are
        // the same entry — same filename, same label — and there is no `<manager>-self-update` file
        // any more. The names and ids here are the ones each agent reports the manager's own row
        // under (system_info::HOMEBREW_NAME, WINGET_NAME, FLATPAK_NAME, ...).
        Assert.True(PackageManagerCatalog.TryGet(managerName, out var manager));
        var bucket = PlatformBucket.ForPackageManager(managerName);

        var fromManagerRow = ApprovedScriptIdentity.For(bucket, manager.BuildScript(), ownRowName, ownRowId);
        var fromManagedRow = ApprovedScriptIdentity.For(bucket, manager.BuildScript(), "Firefox", "org.mozilla.firefox");

        Assert.True(fromManagerRow.IsPackageManagerScript);
        Assert.Equal(fromManagedRow.FileBaseName, fromManagerRow.FileBaseName);
        Assert.Equal(fromManagedRow.DisplayName, fromManagerRow.DisplayName);
        Assert.DoesNotContain("self-update", fromManagerRow.FileBaseName);
        Assert.Null(fromManagerRow.ApplicationIdentifier);
    }

    [Theory]
    [InlineData(PackageManagerCatalog.Homebrew, "homebrew")]
    [InlineData(PackageManagerCatalog.Winget, "winget")]
    [InlineData(PackageManagerCatalog.Chocolatey, "chocolatey")]
    [InlineData(PackageManagerCatalog.Flatpak, "flatpak")]
    [InlineData(PackageManagerCatalog.Snap, "snap")]
    public void For_EveryRecognizedManagersManagedScript_IsNamedAfterThatManager(string managerName, string expected)
    {
        Assert.True(PackageManagerCatalog.TryGet(managerName, out var manager));

        var identity = ApprovedScriptIdentity.For(
            PlatformBucket.ForPackageManager(managerName), manager.BuildScript(), "Firefox", "org.mozilla.firefox");

        Assert.Equal(expected, identity.FileBaseName);
        Assert.True(identity.IsPackageManagerScript);
    }

    [Fact]
    public void For_APackageManagerScript_NeverTakesTheManagersOwnName()
    {
        // Adoption candidates are offered by matching an entry's name against a local row's
        // (GetUpgradeScriptsOverviewQueryHandler). A generic entry called exactly "Homebrew" would
        // match the manager's own row and offer it a script it already holds.
        foreach (var managerName in new[]
        {
            PackageManagerCatalog.Homebrew, PackageManagerCatalog.Winget, PackageManagerCatalog.Chocolatey,
            PackageManagerCatalog.Flatpak, PackageManagerCatalog.Snap,
        })
        {
            Assert.True(PackageManagerCatalog.TryGet(managerName, out var manager));

            var identity = ApprovedScriptIdentity.For(
                PlatformBucket.ForPackageManager(managerName), manager.BuildScript(), managerName, null);

            Assert.NotEqual(managerName, identity.DisplayName);
        }
    }

    [Fact]
    public void For_AnAiResearchedScript_KeepsTheApplicationsOwnIdentity()
    {
        // The opposite case: this content really is one application's, so its own name and
        // identifier are exactly the right label — and the identifier is the more stable half.
        var identity = ApprovedScriptIdentity.For(
            PlatformBucket.MacOs, "#!/bin/bash\n# researched\n", "Nextcloud", "com.nextcloud.desktopclient");

        Assert.False(identity.IsPackageManagerScript);
        Assert.Equal("Nextcloud", identity.DisplayName);
        Assert.Equal("com.nextcloud.desktopclient", identity.FileBaseName);
        Assert.Equal("com.nextcloud.desktopclient", identity.ApplicationIdentifier);
    }

    [Fact]
    public void For_AnAiResearchedScript_KeepsTheIdentifiersCasing()
    {
        // winget knows Firefox as Mozilla.Firefox; a filename that quietly disagreed would be one
        // more thing to reconcile by hand when reading the repository.
        var identity = ApprovedScriptIdentity.For(
            PlatformBucket.Windows, "Set-StrictMode -Version Latest\n", "Firefox", "Mozilla.Firefox");

        Assert.Equal("Mozilla.Firefox", identity.FileBaseName);
    }

    [Fact]
    public void For_AnApplicationWithNoIdentifier_FallsBackToItsName()
    {
        var identity = ApprovedScriptIdentity.For(
            PlatformBucket.MacOs, "#!/bin/bash\n# researched\n", "Visual Studio Code", applicationIdentifier: null);

        Assert.Equal("Visual-Studio-Code", identity.FileBaseName);
    }

    [Fact]
    public void For_APackageManagerBucketHoldingSomethingElse_IsNamedAfterTheApplication()
    {
        // A row under a package-manager bucket whose script is not that manager's generated one —
        // hand-edited, or adopted from elsewhere. It is not the manager's shared script and must not
        // claim to be, since that claim is what tells a reviewer their review covers every
        // application the manager handles.
        var identity = ApprovedScriptIdentity.For(
            WingetBucket, "Set-StrictMode -Version Latest\n# hand-written\n", "Firefox", "Mozilla.Firefox");

        Assert.False(identity.IsPackageManagerScript);
        Assert.Equal("Firefox", identity.DisplayName);
        Assert.Equal("Mozilla.Firefox", identity.FileBaseName);
    }

    [Theory]
    // A path separator would put the script in a directory the reader never looks in.
    [InlineData("../../etc/passwd", "etc-passwd")]
    [InlineData("a/b", "a-b")]
    // A leading dot makes it a hidden file; a name of only dots would be "." or "..".
    [InlineData(".hidden", "hidden")]
    [InlineData("..", "script")]
    // Runs collapse to one separator rather than leaving gaps.
    [InlineData("Visual Studio  Code", "Visual-Studio-Code")]
    // Nothing usable left at all falls back to the historical name, so the entry looks like an old
    // one rather than like a broken one.
    [InlineData("///", "script")]
    [InlineData("日本語", "script")]
    public void For_ReducesAnIdentifierToASafePathComponent(string identifier, string expected)
    {
        var identity = ApprovedScriptIdentity.For(PlatformBucket.MacOs, "#!/bin/bash\n", "An App", identifier);

        Assert.Equal(expected, identity.FileBaseName);
    }

    [Fact]
    public void For_TruncatesAnAbsurdlyLongName_WithoutLeavingATrailingSeparator()
    {
        var identity = ApprovedScriptIdentity.For(
            PlatformBucket.MacOs, "#!/bin/bash\n", "An App", new string('a', 400) + " " + new string('b', 400));

        Assert.Equal(100, identity.FileBaseName.Length);
        Assert.DoesNotContain("-", identity.FileBaseName);
    }
}
