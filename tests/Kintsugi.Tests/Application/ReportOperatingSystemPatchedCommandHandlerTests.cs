using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Hosts.Commands.ReportOperatingSystemPatched;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Tests.Application;

public class ReportOperatingSystemPatchedCommandHandlerTests
{
    private readonly Mock<IHostRepository> _hostRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private ReportOperatingSystemPatchedCommandHandler CreateHandler() => new(_hostRepository.Object, _unitOfWork.Object);

    [Fact]
    public async Task Handle_WhenNoHostWithThatSerialNumberIsRegistered_ThrowsNotFound()
    {
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("MISSING", It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => CreateHandler().Handle(
            new ReportOperatingSystemPatchedCommand("MISSING"), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_ClearsThePendingUpdateFlagAndTargetVersion_AndSaves()
    {
        var host = new Host("host-1", "SERIAL-1", operatingSystemUpdateAvailable: true, operatingSystemLatestVersion: "15.1");
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync(host);

        await CreateHandler().Handle(new ReportOperatingSystemPatchedCommand("SERIAL-1"), CancellationToken.None);

        Assert.False(host.OperatingSystemUpdateAvailable);
        Assert.Null(host.OperatingSystemLatestVersion);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
