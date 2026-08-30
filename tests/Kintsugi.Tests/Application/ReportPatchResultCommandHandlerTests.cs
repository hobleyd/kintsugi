using Moq;
using Kintsugi.Application.Applications.Commands.ReportPatchResult;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Tests.Application;

public class ReportPatchResultCommandHandlerTests
{
    private readonly Mock<IHostRepository> _hostRepository = new();
    private readonly Mock<IInstalledApplicationRepository> _installedApplicationRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Host _host = new("host-1", "SERIAL-1", "macOS 15.0");

    private ReportPatchResultCommandHandler CreateHandler() =>
        new(_hostRepository.Object, _installedApplicationRepository.Object, _unitOfWork.Object);

    [Fact]
    public async Task Handle_WhenNoHostWithThatSerialNumberIsRegistered_ThrowsNotFound()
    {
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("MISSING", It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => CreateHandler().Handle(
            new ReportPatchResultCommand("MISSING", "Firefox", "129.0"), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_UpdatesTheInstalledApplicationsVersion_WhenItIsFound()
    {
        var application = new InstalledApplication(_host.Id, "Firefox", "128.0");
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync(_host);
        _installedApplicationRepository
            .Setup(r => r.GetByHostIdAndNameAsync(_host.Id, "Firefox", It.IsAny<CancellationToken>()))
            .ReturnsAsync(application);

        await CreateHandler().Handle(new ReportPatchResultCommand("SERIAL-1", "Firefox", "129.0"), CancellationToken.None);

        Assert.Equal("129.0", application.Version);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_DoesNothing_WhenTheHostHasNoSuchApplicationReported()
    {
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync(_host);
        _installedApplicationRepository
            .Setup(r => r.GetByHostIdAndNameAsync(_host.Id, "Firefox", It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstalledApplication?)null);

        await CreateHandler().Handle(new ReportPatchResultCommand("SERIAL-1", "Firefox", "129.0"), CancellationToken.None);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
