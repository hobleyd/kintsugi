using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Domain;

public class UpgradePathTests
{
    private static UpgradePath CreatePath(string? script = null, string? command = null) => UpgradePath.Create(
        "Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
        downloadUrl: null, command, instructions: null, sourceUrl: null, notes: null, script);

    [Fact]
    public void Create_RejectsAMissingApplicationName()
    {
        Assert.Throws<DomainException>(() => UpgradePath.Create(
            "", "macOS", UpgradePathStatus.NotFound, null, UpgradeMethod.Unknown, null, null, null, null, null));
    }

    [Fact]
    public void Create_RejectsAMissingPlatform()
    {
        Assert.Throws<DomainException>(() => UpgradePath.Create(
            "Firefox", "", UpgradePathStatus.NotFound, null, UpgradeMethod.Unknown, null, null, null, null, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Create_TreatsWhitespaceOnlyOptionalFieldsAsNull(string blank)
    {
        var path = UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.NotFound, blank, UpgradeMethod.Unknown, blank, blank, blank, blank, blank);

        Assert.Null(path.LatestVersion);
        Assert.Null(path.DownloadUrl);
        Assert.Null(path.Command);
        Assert.Null(path.Instructions);
        Assert.Null(path.SourceUrl);
        Assert.Null(path.Notes);
    }

    [Fact]
    public void SetSignatures_RecordsBothSignaturesAsGiven()
    {
        var path = CreatePath(script: "#!/bin/sh\necho hi\n");

        path.SetSignatures("script-signature", "command-signature");

        Assert.Equal("script-signature", path.ScriptSignature);
        Assert.Equal("command-signature", path.CommandSignature);
    }

    [Fact]
    public void SetSignatures_CanClearAPreviouslySetSignature()
    {
        var path = CreatePath();
        path.SetSignatures("old-script-sig", "old-command-sig");

        path.SetSignatures(null, null);

        Assert.Null(path.ScriptSignature);
        Assert.Null(path.CommandSignature);
    }

    [Fact]
    public void SignScript_RecordsTheScriptSignature_WithoutTouchingTheCommandSignature()
    {
        var path = CreatePath(script: "#!/bin/sh\necho hi\n", command: "brew upgrade firefox");
        path.SetSignatures(null, "command-signature");

        path.SignScript("script-signature");

        Assert.Equal("script-signature", path.ScriptSignature);
        Assert.Equal("command-signature", path.CommandSignature);
    }

    [Fact]
    public void UpdateDiscoveredLatestVersion_UpdatesVersionAndCheckedUtc_WithoutTouchingOtherFields()
    {
        var path = CreatePath(command: "brew upgrade firefox");
        path.SetSignatures("script-sig", "command-sig");
        var checkedBefore = path.CheckedUtc;

        path.UpdateDiscoveredLatestVersion("129.0");

        Assert.Equal("129.0", path.LatestVersion);
        Assert.True(path.CheckedUtc >= checkedBefore);
        // Script/Command (and so their signatures) are untouched by a version-only discovery —
        // this is the whole point of the agent's own --update-version re-check path.
        Assert.Equal("script-sig", path.ScriptSignature);
        Assert.Equal("command-sig", path.CommandSignature);
    }

    [Fact]
    public void UpdateDiscoveredLatestVersion_WithBlankVersion_ClearsIt()
    {
        var path = CreatePath();
        path.UpdateDiscoveredLatestVersion("129.0");

        path.UpdateDiscoveredLatestVersion(" ");

        Assert.Null(path.LatestVersion);
    }

    [Fact]
    public void Update_ReplacesTheApplicationIdentifierOnlyWhenANonBlankOneIsGiven()
    {
        var path = UpgradePath.Create(
            "Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
            null, null, null, null, null, null, applicationIdentifier: "org.mozilla.firefox");

        path.Update(UpgradePathStatus.Found, "129.0", UpgradeMethod.Script, null, null, null, null, null, applicationIdentifier: null);

        Assert.Equal("org.mozilla.firefox", path.ApplicationIdentifier);
    }
}
