using Moq;
using Kintsugi.Application.Authentication.Commands.UpdateAuthenticationSettings;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.Authentication;

public class UpdateAuthenticationSettingsCommandHandlerTests
{
    private readonly Mock<IAuthenticationSettingsRepository> _repository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private UpdateAuthenticationSettingsCommandHandler CreateHandler() => new(_repository.Object, _unitOfWork.Object);

    [Fact]
    public async Task Handle_WhenNoneSavedYet_CreatesThem()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((AuthenticationSettings?)null);

        var result = await CreateHandler().Handle(
            new UpdateAuthenticationSettingsCommand(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, null, true), CancellationToken.None);

        Assert.Equal(AuthProvider.GoogleWorkspace, result.Provider);
        Assert.True(result.HasClientSecret);
        _repository.Verify(r => r.AddAsync(It.IsAny<AuthenticationSettings>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenSettingsAlreadyExist_UpdatesThemInPlace()
    {
        var existing = AuthenticationSettings.Create(AuthProvider.GoogleWorkspace, "client-id", "old-secret", null, null, null, false);
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(
            new UpdateAuthenticationSettingsCommand(AuthProvider.GoogleWorkspace, "client-id", "new-secret", null, null, null, true), CancellationToken.None);

        Assert.True(result.IsEnabled);
        Assert.Equal("new-secret", existing.ClientSecret);
        _repository.Verify(r => r.AddAsync(It.IsAny<AuthenticationSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NeverReturnsTheRawClientSecret_OnlyWhetherOneIsStored()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((AuthenticationSettings?)null);

        var result = await CreateHandler().Handle(
            new UpdateAuthenticationSettingsCommand(AuthProvider.GoogleWorkspace, "client-id", "super-secret-value", null, null, null, true),
            CancellationToken.None);

        var resultText = result.ToString();
        Assert.DoesNotContain("super-secret-value", resultText);
        Assert.True(result.HasClientSecret);
    }
}
