using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Hosts.Commands.CreateHost;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application;

public class CreateHostCommandHandlerTests
{
    private readonly Mock<IHostRepository> _hostRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ICheckInLoadBalancer> _loadBalancer = new();

    public CreateHostCommandHandlerTests()
    {
        _loadBalancer.Setup(l => l.RecordCheckIn(It.IsAny<string>(), It.IsAny<int>())).Returns((int?)null);
        // The common case: nothing else holds the hostname. Tests that care override this.
        _hostRepository.Setup(r => r.GetByHostnameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);
    }

    /// <summary>A host as it looks after an admin pressed Remove but the agent never confirmed —
    /// soft-deleted, invisible on the hosts list, and still holding its hostname in the unique
    /// index. See <see cref="Host.RequestRemoval"/>.</summary>
    private static Host RemovedHost(string hostname, string serialNumber)
    {
        var host = new Host(hostname, serialNumber);
        host.RequestRemoval();
        return host;
    }

    private CreateHostCommandHandler CreateHandler() => new(_hostRepository.Object, _unitOfWork.Object, _loadBalancer.Object);

    [Fact]
    public async Task Handle_WhenNoHostWithThatSerialNumberExists_CreatesAndSavesANewOne()
    {
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);

        var result = await CreateHandler().Handle(new CreateHostCommand("host-1", "SERIAL-1", OperatingSystem: "macOS 15.0"), CancellationToken.None);

        Assert.True(result.WasCreated);
        Assert.Equal("host-1", result.Host.Hostname);
        Assert.Equal(HostStatus.Online, result.Host.Status);
        _hostRepository.Verify(r => r.AddAsync(It.IsAny<Host>(), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenAHostWithThatSerialNumberAlreadyExists_ReregistersItInstead()
    {
        var existing = new Host("old-name", "SERIAL-1", "macOS 14.0");
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(new CreateHostCommand("new-name", "SERIAL-1", OperatingSystem: "macOS 15.0"), CancellationToken.None);

        Assert.False(result.WasCreated);
        Assert.Equal("new-name", existing.Hostname);
        Assert.Equal("macOS 15.0", existing.OperatingSystem);
        _hostRepository.Verify(r => r.AddAsync(It.IsAny<Host>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_PassesTheOsUpdateFieldsThrough_OnBothCreateAndReregister()
    {
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);

        var result = await CreateHandler().Handle(
            new CreateHostCommand(
                "host-1", "SERIAL-1", OperatingSystem: "macOS 15.0", OperatingSystemUpdateAvailable: true, OperatingSystemLatestVersion: "15.1"),
            CancellationToken.None);

        Assert.True(result.Host.OperatingSystemUpdateAvailable);
        Assert.Equal("15.1", result.Host.OperatingSystemLatestVersion);
    }

    [Fact]
    public async Task Handle_PassesTheAgentVersionThrough_OnBothCreateAndReregister()
    {
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);

        var created = await CreateHandler().Handle(
            new CreateHostCommand("host-1", "SERIAL-1", AgentVersion: "0.6.0"), CancellationToken.None);

        Assert.Equal("0.6.0", created.Host.AgentVersion);

        var existing = new Host("host-1", "SERIAL-1", agentVersion: "0.6.0");
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var reregistered = await CreateHandler().Handle(
            new CreateHostCommand("host-1", "SERIAL-1", AgentVersion: "0.6.1"), CancellationToken.None);

        Assert.Equal("0.6.1", reregistered.Host.AgentVersion);
    }

    [Fact]
    public async Task Handle_PassesTheSerialNumberAndReportedCheckInMinuteToTheLoadBalancer()
    {
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);

        await CreateHandler().Handle(new CreateHostCommand("host-1", "SERIAL-1", CheckInMinute: 42), CancellationToken.None);

        _loadBalancer.Verify(l => l.RecordCheckIn("SERIAL-1", 42), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenTheLoadBalancerSuggestsADifferentMinute_ReturnsItInTheResult()
    {
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);
        _loadBalancer.Setup(l => l.RecordCheckIn(It.IsAny<string>(), It.IsAny<int>())).Returns(17);

        var result = await CreateHandler().Handle(new CreateHostCommand("host-1", "SERIAL-1"), CancellationToken.None);

        Assert.Equal(17, result.SuggestedCheckInMinute);
    }

    [Fact]
    public async Task Handle_WhenTheHostnameIsHeldByARemovedHost_DeletesItAndRegistersTheNewOne()
    {
        // The machine came back with a different identity than it left with — re-imaged, or its
        // serial number moved between rungs of the agent's fallback chain. The removed row is
        // invisible but still owns the name, so without this the insert is a bare 500.
        var removed = RemovedHost("host-1", "OLD-SERIAL");
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("NEW-SERIAL", It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);
        _hostRepository.Setup(r => r.GetByHostnameAsync("host-1", It.IsAny<CancellationToken>())).ReturnsAsync(removed);

        var result = await CreateHandler().Handle(new CreateHostCommand("host-1", "NEW-SERIAL"), CancellationToken.None);

        Assert.True(result.WasCreated);
        Assert.Equal("NEW-SERIAL", result.Host.SerialNumber);
        _hostRepository.Verify(r => r.DeleteAsync(removed, It.IsAny<CancellationToken>()), Times.Once);
        _hostRepository.Verify(r => r.AddAsync(It.IsAny<Host>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenTheHostnameIsHeldByALiveHost_ReportsAConflictRatherThanDeletingIt()
    {
        // Two machines really do claim one name. Deleting either record on an agent's say-so would
        // be the wrong call, so this is reported instead — as a 409, not the 500 an unhandled
        // unique-index violation produces.
        var live = new Host("host-1", "OTHER-SERIAL");
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("NEW-SERIAL", It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);
        _hostRepository.Setup(r => r.GetByHostnameAsync("host-1", It.IsAny<CancellationToken>())).ReturnsAsync(live);

        var exception = await Assert.ThrowsAsync<ConflictException>(
            () => CreateHandler().Handle(new CreateHostCommand("host-1", "NEW-SERIAL"), CancellationToken.None));

        Assert.Contains("OTHER-SERIAL", exception.Message);
        _hostRepository.Verify(r => r.DeleteAsync(It.IsAny<Host>(), It.IsAny<CancellationToken>()), Times.Never);
        _hostRepository.Verify(r => r.AddAsync(It.IsAny<Host>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenAHostPendingRemovalReregistersUnderItsOwnSerialNumber_LeavesItPendingRatherThanReclaiming()
    {
        // The guardrail on the reclaim above: the ordinary two-phase removal must still work. This
        // host is matched by serial number, so it keeps RemovalRequested and its next check-in
        // response still tells it to uninstall itself — rather than being quietly resurrected.
        var removed = RemovedHost("host-1", "SERIAL-1");
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync(removed);

        var result = await CreateHandler().Handle(new CreateHostCommand("host-1", "SERIAL-1"), CancellationToken.None);

        Assert.False(result.WasCreated);
        Assert.True(removed.RemovalRequested);
        Assert.NotNull(removed.DeletedAtUtc);
        _hostRepository.Verify(r => r.DeleteAsync(It.IsAny<Host>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenAKnownHostReregistersUnderItsOwnUnchangedHostname_DoesNotDeleteItself()
    {
        // GetByHostnameAsync returns the very host being re-registered; reclaiming it would delete
        // the row this check-in is updating.
        var existing = new Host("host-1", "SERIAL-1");
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _hostRepository.Setup(r => r.GetByHostnameAsync("host-1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        await CreateHandler().Handle(new CreateHostCommand("host-1", "SERIAL-1"), CancellationToken.None);

        _hostRepository.Verify(r => r.DeleteAsync(It.IsAny<Host>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenARenamedHostTakesANameARemovedHostHeld_ReclaimsItRatherThanFailing()
    {
        // Same serial number, new hostname: matched by serial, so it re-registers — but the name it
        // is moving onto is held by a removed row, which is the same unique index.
        var existing = new Host("old-name", "SERIAL-1");
        var removed = RemovedHost("new-name", "GONE-SERIAL");
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _hostRepository.Setup(r => r.GetByHostnameAsync("new-name", It.IsAny<CancellationToken>())).ReturnsAsync(removed);

        await CreateHandler().Handle(new CreateHostCommand("new-name", "SERIAL-1"), CancellationToken.None);

        Assert.Equal("new-name", existing.Hostname);
        _hostRepository.Verify(r => r.DeleteAsync(removed, It.IsAny<CancellationToken>()), Times.Once);
    }
}
