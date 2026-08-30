using Moq;
using Kintsugi.Application.AiSettings.Commands.UpdateAiAgentSettings;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.AiSettings;

public class UpdateAiAgentSettingsCommandHandlerTests
{
    private readonly Mock<IAiAgentSettingsRepository> _repository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private UpdateAiAgentSettingsCommandHandler CreateHandler() => new(_repository.Object, _unitOfWork.Object);

    [Fact]
    public async Task Handle_WhenNoneSavedYet_CreatesThem()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((AiAgentSettings?)null);

        var result = await CreateHandler().Handle(
            new UpdateAiAgentSettingsCommand(AiProvider.Anthropic, "sk-123", null, "claude-sonnet-5", true), CancellationToken.None);

        Assert.Equal(AiProvider.Anthropic, result.Provider);
        Assert.True(result.HasApiKey);
        _repository.Verify(r => r.AddAsync(It.IsAny<AiAgentSettings>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenSettingsAlreadyExist_UpdatesThemInPlace()
    {
        var existing = AiAgentSettings.Create(AiProvider.Anthropic, "sk-old", null, null, false);
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(
            new UpdateAiAgentSettingsCommand(AiProvider.Anthropic, "sk-new", null, "claude-sonnet-5", true), CancellationToken.None);

        Assert.True(result.IsEnabled);
        Assert.Equal("sk-new", existing.ApiKey);
        _repository.Verify(r => r.AddAsync(It.IsAny<AiAgentSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NeverReturnsTheRawApiKey_OnlyWhetherOneIsStored()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((AiAgentSettings?)null);

        var result = await CreateHandler().Handle(
            new UpdateAiAgentSettingsCommand(AiProvider.Anthropic, "sk-super-secret", null, null, true), CancellationToken.None);

        var resultText = result.ToString();
        Assert.DoesNotContain("sk-super-secret", resultText);
        Assert.True(result.HasApiKey);
    }
}
