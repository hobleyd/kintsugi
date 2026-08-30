using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.PatchingPolicy.Queries.GetPatchingPolicySettings;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.PatchingPolicy;

public class GetPatchingPolicySettingsQueryHandlerTests
{
    private readonly Mock<IPatchingPolicySettingsRepository> _repository = new();

    private GetPatchingPolicySettingsQueryHandler CreateHandler() => new(_repository.Object);

    [Fact]
    public async Task Handle_WhenNoneSavedYet_ReturnsSensibleDefaults()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PatchingPolicySettings?)null);

        var result = await CreateHandler().Handle(new GetPatchingPolicySettingsQuery(), CancellationToken.None);

        Assert.Equal(7, result.IntervalValue);
        Assert.Equal(PatchingTimeUnit.Days, result.IntervalUnit);
    }

    [Fact]
    public async Task Handle_WhenSettingsExist_ReturnsThem()
    {
        var settings = PatchingPolicySettings.Create(1, PatchingTimeUnit.Hours, 4, PatchingTimeUnit.Hours, 0);
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

        var result = await CreateHandler().Handle(new GetPatchingPolicySettingsQuery(), CancellationToken.None);

        Assert.Equal(1, result.IntervalValue);
        Assert.Equal(PatchingTimeUnit.Hours, result.IntervalUnit);
        Assert.Equal(0, result.MaxDelayCount);
    }
}
