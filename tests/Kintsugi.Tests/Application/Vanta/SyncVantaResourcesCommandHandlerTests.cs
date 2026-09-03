using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.Vanta;
using Kintsugi.Application.Vanta.Commands.SyncVantaResources;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.Vanta;

public class SyncVantaResourcesCommandHandlerTests
{
    private readonly Mock<IVantaSettingsProvider> _settings = new();
    private readonly Mock<IHostRepository> _hosts = new();
    private readonly Mock<IUpgradePathRepository> _upgradePaths = new();
    private readonly Mock<IVantaSyncClient> _client = new();

    private static readonly VantaSettingsSnapshot Configured = new(
        true, "client", "secret", "https://api.vanta.com", "vc-1", "pv-1", "https://kintsugi.example.com", 5.0d, 24);

    public SyncVantaResourcesCommandHandlerTests()
    {
        _settings.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Configured);
        _hosts.Setup(h => h.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { CheckedInHost() });
        _upgradePaths.Setup(r => r.GetOutdatedStatusesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UpgradeStatusDto>());
    }

    private static Host CheckedInHost(string hostname = "mac-01", string serial = "C02ABC123")
    {
        var host = new Host(hostname, serial, "macOS 14.5");
        host.RecordHeartbeat(HostStatus.Online);
        return host;
    }

    private SyncVantaResourcesCommandHandler Handler() =>
        new(_settings.Object, _hosts.Object, _upgradePaths.Object, _client.Object);

    [Fact]
    public async Task Handle_SendsComponentsBeforePackages()
    {
        var order = new List<string>();
        _client.Setup(c => c.SyncVulnerableComponentsAsync(It.IsAny<IReadOnlyList<VantaVulnerableComponent>>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("components")).Returns(Task.CompletedTask);
        _client.Setup(c => c.SyncPackageVulnerabilitiesAsync(It.IsAny<IReadOnlyList<VantaPackageVulnerability>>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("packages")).Returns(Task.CompletedTask);

        var result = await Handler().Handle(new SyncVantaResourcesCommand(), CancellationToken.None);

        // Each package names its component by uniqueId, so components have to land first.
        Assert.Equal(new[] { "components", "packages" }, order);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Handle_DoesNotSendPackagesWhenTheComponentSyncFails()
    {
        _client.Setup(c => c.SyncVulnerableComponentsAsync(It.IsAny<IReadOnlyList<VantaVulnerableComponent>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExternalServiceException("Vanta rejected the VulnerableComponent sync (500)."));

        var result = await Handler().Handle(new SyncVantaResourcesCommand(), CancellationToken.None);

        // Packages sent against components Vanta does not hold are orphans at best.
        _client.Verify(
            c => c.SyncPackageVulnerabilitiesAsync(It.IsAny<IReadOnlyList<VantaPackageVulnerability>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.True(result.Attempted);
        Assert.False(result.Succeeded);
        Assert.Contains("rejected", result.Message);
    }

    [Fact]
    public async Task Handle_SendsNothingWhenNoHostWouldBeSynced()
    {
        _hosts.Setup(h => h.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Host>());

        var result = await Handler().Handle(new SyncVantaResourcesCommand(), CancellationToken.None);

        // An empty component list would tell Vanta every host this server ever reported has ceased to
        // exist, taking every vulnerability recorded against them with it. "Don't send" is the safe
        // reading of zero hosts; a genuinely empty fleet has nothing to sync anyway.
        _client.VerifyNoOtherCalls();
        Assert.False(result.Attempted);
        Assert.Contains("No hosts are enrolled", result.Message);
    }

    [Fact]
    public async Task Handle_SendsAnEmptyPackageListWhenTheFleetIsFullyPatched()
    {
        IReadOnlyList<VantaPackageVulnerability>? sent = null;
        _client.Setup(c => c.SyncPackageVulnerabilitiesAsync(It.IsAny<IReadOnlyList<VantaPackageVulnerability>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<VantaPackageVulnerability>, CancellationToken>((p, _) => sent = p)
            .Returns(Task.CompletedTask);

        var result = await Handler().Handle(new SyncVantaResourcesCommand(), CancellationToken.None);

        // The mirror image of the guard above, and just as important: an empty *package* list is how
        // a fleet that has finished patching clears what Vanta is still holding for it.
        Assert.NotNull(sent);
        Assert.Empty(sent!);
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.PackageCount);
    }

    [Fact]
    public async Task Handle_SendsNothingWhenTheIntegrationIsSwitchedOff()
    {
        _settings.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Configured with { Enabled = false });

        var result = await Handler().Handle(new SyncVantaResourcesCommand(), CancellationToken.None);

        _client.VerifyNoOtherCalls();
        Assert.False(result.Attempted);
        Assert.Contains("switched off", result.Message);
    }

    [Fact]
    public async Task Handle_SaysSoWhenEnabledButNotFullyConfigured()
    {
        _settings.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Configured with { PackageVulnerabilityResourceId = null });

        var result = await Handler().Handle(new SyncVantaResourcesCommand(), CancellationToken.None);

        // Otherwise this is a nightly job that quietly does nothing.
        _client.VerifyNoOtherCalls();
        Assert.False(result.Attempted);
        Assert.Contains("not completely configured", result.Message);
    }
}
