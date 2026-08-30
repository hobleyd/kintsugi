using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.UpgradePaths;

public class UpgradePathStatusKeyTests
{
    private static UpgradePathSummaryDto Path(
        UpgradePathStatus status = UpgradePathStatus.Found,
        UpgradeMethod method = UpgradeMethod.DirectDownload,
        string? script = null,
        string? scriptSignature = null,
        int updateAvailableHostCount = 0) =>
        new(
            "Firefox",
            "macOS",
            status,
            "128.0",
            method,
            "https://example.com/firefox.dmg",
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            HostCount: 3,
            UpToDateHostCount: 3 - updateAvailableHostCount,
            UpdateAvailableHostCount: updateAvailableHostCount,
            HostNamesNeedingUpdate: Array.Empty<string>(),
            Script: script,
            ScriptSignature: scriptSignature);

    [Fact]
    public void For_FailedStatus_ReturnsCheckFailed()
    {
        Assert.Equal(UpgradePathStatusKey.CheckFailed, UpgradePathStatusKey.For(Path(status: UpgradePathStatus.Failed)));
    }

    [Fact]
    public void For_NotFoundStatus_ReturnsNotFound()
    {
        Assert.Equal(UpgradePathStatusKey.NotFound, UpgradePathStatusKey.For(Path(status: UpgradePathStatus.NotFound)));
    }

    [Fact]
    public void For_UnsignedScript_ReturnsReviewAndSign()
    {
        var path = Path(method: UpgradeMethod.Script, script: "#!/bin/bash\n...", scriptSignature: null);

        Assert.Equal(UpgradePathStatusKey.ReviewAndSign, UpgradePathStatusKey.For(path));
    }

    [Fact]
    public void For_SignedScript_WithNoHostsNeedingUpdate_ReturnsUpToDate()
    {
        var path = Path(method: UpgradeMethod.Script, script: "#!/bin/bash\n...", scriptSignature: "sig", updateAvailableHostCount: 0);

        Assert.Equal(UpgradePathStatusKey.UpToDate, UpgradePathStatusKey.For(path));
    }

    [Fact]
    public void For_HostsNeedingUpdate_ReturnsUpdateAvailable()
    {
        var path = Path(updateAvailableHostCount: 1);

        Assert.Equal(UpgradePathStatusKey.UpdateAvailable, UpgradePathStatusKey.For(path));
    }

    [Fact]
    public void For_NoHostsNeedingUpdate_ReturnsUpToDate()
    {
        var path = Path(updateAvailableHostCount: 0);

        Assert.Equal(UpgradePathStatusKey.UpToDate, UpgradePathStatusKey.For(path));
    }
}
