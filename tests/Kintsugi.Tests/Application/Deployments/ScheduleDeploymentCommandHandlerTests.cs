using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Deployments.Commands.ScheduleDeployment;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Tests.Application.Deployments;

public class ScheduleDeploymentCommandHandlerTests
{
    private readonly Mock<IPatchDeploymentRepository> _repository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    [Fact]
    public async Task Handle_AddsAndPersistsTheNewDeployment()
    {
        var handler = new ScheduleDeploymentCommandHandler(_repository.Object, _unitOfWork.Object);
        var hostId = Guid.NewGuid();
        var patchId = Guid.NewGuid();
        var scheduledFor = DateTimeOffset.UtcNow.AddDays(1);

        var result = await handler.Handle(new ScheduleDeploymentCommand(hostId, patchId, scheduledFor), CancellationToken.None);

        Assert.Equal(hostId, result.HostId);
        Assert.Equal(patchId, result.PatchId);
        _repository.Verify(r => r.AddAsync(It.Is<PatchDeployment>(d => d.HostId == hostId && d.PatchId == patchId), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
