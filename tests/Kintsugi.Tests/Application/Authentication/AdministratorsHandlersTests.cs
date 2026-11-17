using Moq;
using Kintsugi.Application.Authentication.Commands.AddAdministrators;
using Kintsugi.Application.Authentication.Commands.RemoveAdministrator;
using Kintsugi.Application.Authentication.Queries.GetAdministrators;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Application.Authentication;

public class AdministratorsHandlersTests
{
    private readonly Mock<IAuthenticationSettingsRepository> _repository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private AuthenticationSettings Stored(params string[] admins)
    {
        var settings = AuthenticationSettings.Create(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, null, true, admins);
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        return settings;
    }

    private void NothingStored() =>
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((AuthenticationSettings?)null);

    [Fact]
    public async Task Get_WhenNothingIsConfigured_IsEmpty()
    {
        NothingStored();

        var result = await new GetAdministratorsQueryHandler(_repository.Object).Handle(new GetAdministratorsQuery(), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Get_ListsEveryAdministrator_WithTheirRecord()
    {
        var settings = Stored();
        settings.AddAdmins(new[] { "a@example.invalid" }, "juan@example.invalid", DateTimeOffset.UtcNow);

        var result = await new GetAdministratorsQueryHandler(_repository.Object).Handle(new GetAdministratorsQuery(), CancellationToken.None);

        var dto = Assert.Single(result);
        Assert.Equal("a@example.invalid", dto.Email);
        Assert.Equal("juan@example.invalid", dto.AddedBy);
    }

    [Fact]
    public async Task Add_SavesAndReturnsTheNewList_RecordingTheCaller()
    {
        Stored("juan@example.invalid");

        var result = await new AddAdministratorsCommandHandler(_repository.Object, _unitOfWork.Object).Handle(
            new AddAdministratorsCommand(new[] { "jake@example.invalid" }, "juan@example.invalid"), CancellationToken.None);

        Assert.Equal(new[] { "juan@example.invalid", "jake@example.invalid" }, result.Select(r => r.Email));
        Assert.Equal("juan@example.invalid", result[1].AddedBy);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_WithNoSignInConfigured_Throws_RatherThanInventingARow()
    {
        NothingStored();

        await Assert.ThrowsAsync<DomainException>(() => new AddAdministratorsCommandHandler(_repository.Object, _unitOfWork.Object).Handle(
            new AddAdministratorsCommand(new[] { "a@example.invalid" }), CancellationToken.None));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Remove_TakesOneOffTheList_AndSaves()
    {
        Stored("a@example.invalid", "b@example.invalid");

        var result = await new RemoveAdministratorCommandHandler(_repository.Object, _unitOfWork.Object).Handle(
            new RemoveAdministratorCommand("A@example.invalid"), CancellationToken.None);

        Assert.Equal("b@example.invalid", Assert.Single(result).Email);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Remove_OfTheLastOne_IsRefused_AndDoesNotSave()
    {
        Stored("a@example.invalid");

        await Assert.ThrowsAsync<DomainException>(() => new RemoveAdministratorCommandHandler(_repository.Object, _unitOfWork.Object).Handle(
            new RemoveAdministratorCommand("a@example.invalid"), CancellationToken.None));

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Remove_WhenNothingIsConfigured_IsEmptyAndDoesNotSave()
    {
        NothingStored();

        var result = await new RemoveAdministratorCommandHandler(_repository.Object, _unitOfWork.Object).Handle(
            new RemoveAdministratorCommand("a@example.invalid"), CancellationToken.None);

        Assert.Empty(result);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("name@example.invalid", true)]
    [InlineData("Jake Uren <jake@example.invalid>", false)]
    [InlineData("not-an-email", false)]
    [InlineData("a b@example.invalid", false)]
    public void Validator_AcceptsOnlyPlainAddresses(string email, bool valid)
    {
        var result = new AddAdministratorsCommandValidator().Validate(new AddAdministratorsCommand(new[] { email }));

        Assert.Equal(valid, result.IsValid);
    }

    [Fact]
    public void Validator_RefusesAnEmptyRequest()
    {
        Assert.False(new AddAdministratorsCommandValidator().Validate(new AddAdministratorsCommand(Array.Empty<string>())).IsValid);
    }
}
