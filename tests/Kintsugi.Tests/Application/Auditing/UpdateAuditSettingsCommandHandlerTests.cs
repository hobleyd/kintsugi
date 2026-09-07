using Moq;
using Kintsugi.Application.Auditing.Commands.UpdateAuditSettings;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Application.Auditing;

/// <summary>
/// The handler's own decisions only — create against update, and the order it clears a secret in.
/// What each provider requires, and the rule that changing provider drops the stored secret, are
/// <see cref="AuditSettings"/>'s and are pinned by <c>AuditSettingsTests</c>.
/// </summary>
public class UpdateAuditSettingsCommandHandlerTests
{
    private readonly Mock<IAuditSettingsRepository> _repository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private UpdateAuditSettingsCommandHandler CreateHandler() => new(_repository.Object, _unitOfWork.Object);

    private void Stored(AuditSettings? settings) =>
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

    private static UpdateAuditSettingsCommand Datadog(string? secret = "dd-api-key", bool clearSecret = false) =>
        new(AuditProvider.Datadog, IsEnabled: true, null, "datadoghq.eu", null, secret, clearSecret,
            null, null, null, null, null, null);

    private static AuditSettings StoredDatadog() =>
        AuditSettings.Create(
            AuditProvider.Datadog, true, null, null, null, "dd-api-key", null, null, null, null, null, null);

    private static AuditSettings StoredLoki() =>
        AuditSettings.Create(
            AuditProvider.GrafanaLoki, true, "https://logs-prod-1.grafana.net", null, "123456", "loki-token",
            null, null, null, null, null, null);

    [Fact]
    public async Task Handle_WhenNoneSavedYet_CreatesThem()
    {
        Stored(null);

        var result = await CreateHandler().Handle(Datadog(), CancellationToken.None);

        Assert.Equal(AuditProvider.Datadog, result.Provider);
        Assert.Equal("datadoghq.eu", result.Region);
        Assert.True(result.HasSecret);
        _repository.Verify(r => r.AddAsync(It.IsAny<AuditSettings>(), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenSettingsAlreadyExist_UpdatesThemInPlace()
    {
        var existing = StoredDatadog();
        Stored(existing);

        var result = await CreateHandler().Handle(Datadog(secret: "rotated-key"), CancellationToken.None);

        Assert.Equal("rotated-key", existing.Secret);
        Assert.Equal("datadoghq.eu", result.Region);
        _repository.Verify(r => r.AddAsync(It.IsAny<AuditSettings>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithABlankSecret_KeepsTheStoredOne()
    {
        // The page never received the real value, so a blank box means "leave it alone" rather than
        // "remove it" — the whole reason ClearSecret is a separate flag.
        var existing = StoredDatadog();
        Stored(existing);

        var result = await CreateHandler().Handle(Datadog(secret: null), CancellationToken.None);

        Assert.Equal("dd-api-key", existing.Secret);
        Assert.True(result.HasSecret);
    }

    [Fact]
    public async Task Handle_WithClearSecret_RemovesItWhereTheProviderAllowsNone()
    {
        var existing = StoredLoki();
        Stored(existing);

        var result = await CreateHandler().Handle(
            new UpdateAuditSettingsCommand(
                AuditProvider.GrafanaLoki, IsEnabled: true, "https://logs-prod-1.grafana.net", null, "123456",
                Secret: null, ClearSecret: true, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.Null(existing.Secret);
        Assert.False(result.HasSecret);
    }

    [Fact]
    public async Task Handle_ClearsTheSecretBeforeUpdateChecksForOne()
    {
        // The clear has to happen first, or Update would be satisfied by the very secret it is in
        // the middle of removing and Datadog would be left enabled with no key. Refusing is the
        // correct answer here: a provider that requires a secret cannot be saved without one.
        var existing = StoredDatadog();
        Stored(existing);

        await Assert.ThrowsAsync<DomainException>(() =>
            CreateHandler().Handle(Datadog(secret: null, clearSecret: true), CancellationToken.None));

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NeverReturnsTheRawSecret_OnlyWhetherOneIsStored()
    {
        Stored(null);

        var result = await CreateHandler().Handle(Datadog(secret: "super-secret-value"), CancellationToken.None);

        Assert.DoesNotContain("super-secret-value", result.ToString());
        Assert.True(result.HasSecret);
    }
}
