using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Hosts.Commands.RequestHostRemoval;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Tests.Application;

public class RequestHostRemovalCommandHandlerTests
{
    private readonly Mock<IHostRepository> _hostRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private RequestHostRemovalCommandHandler CreateHandler() => new(_hostRepository.Object, _unitOfWork.Object);

    [Fact]
    public async Task Handle_WhenNoHostWithThatIdExists_ThrowsNotFound()
    {
        var id = Guid.NewGuid();
        _hostRepository.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => CreateHandler().Handle(new RequestHostRemovalCommand(id), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_MarksTheHostForRemoval_AndSaves()
    {
        var host = new Host("host-1", "SERIAL-1");
        _hostRepository.Setup(r => r.GetByIdAsync(host.Id, It.IsAny<CancellationToken>())).ReturnsAsync(host);

        await CreateHandler().Handle(new RequestHostRemovalCommand(host.Id), CancellationToken.None);

        Assert.True(host.RemovalRequested);
        Assert.NotNull(host.DeletedAtUtc);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
