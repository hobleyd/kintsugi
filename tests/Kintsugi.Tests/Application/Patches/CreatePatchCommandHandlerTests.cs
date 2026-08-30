using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Patches.Commands.CreatePatch;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.Patches;

public class CreatePatchCommandHandlerTests
{
    private readonly Mock<IPatchRepository> _repository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    [Fact]
    public async Task Handle_AddsAndPersistsTheNewPatch()
    {
        var handler = new CreatePatchCommandHandler(_repository.Object, _unitOfWork.Object);
        var releasedUtc = DateTimeOffset.UtcNow;

        var result = await handler.Handle(
            new CreatePatchCommand("Security Update 2026-001", "Apple", "15.1", PatchSeverity.Critical, releasedUtc, "Fixes a kernel vulnerability."),
            CancellationToken.None);

        Assert.Equal("Security Update 2026-001", result.Name);
        Assert.Equal(PatchSeverity.Critical, result.Severity);
        _repository.Verify(r => r.AddAsync(It.Is<Patch>(p => p.Name == "Security Update 2026-001"), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
