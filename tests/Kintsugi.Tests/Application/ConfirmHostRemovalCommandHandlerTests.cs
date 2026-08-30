using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Hosts.Commands.ConfirmHostRemoval;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Application;

public class ConfirmHostRemovalCommandHandlerTests
{
    private readonly Mock<IHostRepository> _hostRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private ConfirmHostRemovalCommandHandler CreateHandler() => new(_hostRepository.Object, _unitOfWork.Object);

    [Fact]
    public async Task Handle_WhenNoHostWithThatSerialNumberIsRegistered_ThrowsNotFound()
    {
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("MISSING", It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => CreateHandler().Handle(new ConfirmHostRemovalCommand("MISSING"), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_WhenRemovalWasNeverRequested_ThrowsDomainException()
    {
        var host = new Host("host-1", "SERIAL-1");
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync(host);

        await Assert.ThrowsAsync<DomainException>(() => CreateHandler().Handle(new ConfirmHostRemovalCommand("SERIAL-1"), CancellationToken.None));

        _hostRepository.Verify(r => r.DeleteAsync(It.IsAny<Host>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenRemovalWasRequested_DeletesTheHost_AndSaves()
    {
        var host = new Host("host-1", "SERIAL-1");
        host.RequestRemoval();
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync(host);

        await CreateHandler().Handle(new ConfirmHostRemovalCommand("SERIAL-1"), CancellationToken.None);

        _hostRepository.Verify(r => r.DeleteAsync(host, It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
