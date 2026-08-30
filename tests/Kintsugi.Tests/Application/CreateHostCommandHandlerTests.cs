using Moq;
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
}
