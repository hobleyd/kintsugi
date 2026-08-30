using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.PatchingPolicy.Commands.UpdatePatchingPolicySettings;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.PatchingPolicy;

public class UpdatePatchingPolicySettingsCommandHandlerTests
{
    private readonly Mock<IPatchingPolicySettingsRepository> _repository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private UpdatePatchingPolicySettingsCommandHandler CreateHandler() => new(_repository.Object, _unitOfWork.Object);

    [Fact]
    public async Task Handle_WhenNoSettingsExistYet_CreatesThem()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PatchingPolicySettings?)null);

        var result = await CreateHandler().Handle(
            new UpdatePatchingPolicySettingsCommand(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3), CancellationToken.None);

        Assert.Equal(7, result.IntervalValue);
        _repository.Verify(r => r.AddAsync(It.IsAny<PatchingPolicySettings>(), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenSettingsAlreadyExist_UpdatesThemInPlace()
    {
        var existing = PatchingPolicySettings.Create(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3);
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(
            new UpdatePatchingPolicySettingsCommand(14, PatchingTimeUnit.Days, 2, PatchingTimeUnit.Days, 5), CancellationToken.None);

        Assert.Equal(14, result.IntervalValue);
        Assert.Equal(14, existing.IntervalValue);
        _repository.Verify(r => r.AddAsync(It.IsAny<PatchingPolicySettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
