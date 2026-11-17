using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Kintsugi.Application.Authentication;
using Kintsugi.Application.Authentication.Commands.AddAdministrators;
using Kintsugi.Application.Authentication.Commands.RemoveAdministrator;
using Kintsugi.Application.Authentication.Queries.GetAuthenticationSettings;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Exceptions;
using Kintsugi.WebApi.Controllers;

namespace Kintsugi.Tests.WebApi;

/// <summary>
/// The Administrators page's add and delete are held to the same lockout guard as saving the list
/// from the Authentication screen: while sign-in is required, a change that leaves a non-empty list
/// without the caller is refused, because their next request would be a 403.
/// </summary>
public class AdminSettingsAdministratorsLockoutTests
{
    private const string Me = "me@example.invalid";
    private readonly Mock<ISender> _sender = new();

    private AdminSettingsController Controller(bool signInRequired, params string[] storedAdmins)
    {
        _sender.Setup(s => s.Send(It.IsAny<GetAuthenticationSettingsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthenticationSettingsDto.NotConfigured() with { IsEnabled = signInRequired, AdminEmails = storedAdmins });
        _sender.Setup(s => s.Send(It.IsAny<AddAdministratorsCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AdministratorDto>());
        _sender.Setup(s => s.Send(It.IsAny<RemoveAdministratorCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AdministratorDto>());

        var identity = new ClaimsIdentity(
            new[] { new Claim("email", Me), new Claim("email_verified", "true"), new Claim("hd", "example.invalid") }, "TestAuth");
        return new AdminSettingsController(
            _sender.Object, Mock.Of<IOptionsMonitorCache<OpenIdConnectOptions>>(), Mock.Of<IVantaSyncCoordinator>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } }
        };
    }

    private void VerifyAdded(Times times) =>
        _sender.Verify(s => s.Send(It.IsAny<AddAdministratorsCommand>(), It.IsAny<CancellationToken>()), times);

    private void VerifyRemoved(Times times) =>
        _sender.Verify(s => s.Send(It.IsAny<RemoveAdministratorCommand>(), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task Add_RecordsTheSignedInCallerAsTheAdder_NotAnythingFromTheBody()
    {
        var controller = Controller(true, Me);

        await controller.AddAdministrators(new AddAdministratorsRequest(new[] { "jake@example.invalid" }), CancellationToken.None);

        _sender.Verify(s => s.Send(
            It.Is<AddAdministratorsCommand>(c => c.AddedBy == Me && c.Emails.Single() == "jake@example.invalid"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_ToAnEmptyList_IsRefused_WhenTheNewListWouldNotIncludeTheCaller()
    {
        var controller = Controller(true);

        await Assert.ThrowsAsync<DomainException>(() =>
            controller.AddAdministrators(new AddAdministratorsRequest(new[] { "jake@example.invalid" }), CancellationToken.None));
        VerifyAdded(Times.Never());
    }

    [Fact]
    public async Task Add_OfTheCallerThemselves_ToAnEmptyList_IsAllowed()
    {
        var controller = Controller(true);

        await controller.AddAdministrators(new AddAdministratorsRequest(new[] { "ME@example.invalid" }), CancellationToken.None);

        VerifyAdded(Times.Once());
    }

    [Fact]
    public async Task Add_IsAllowed_WhenSignInIsNotRequired()
    {
        var controller = Controller(false);

        await controller.AddAdministrators(new AddAdministratorsRequest(new[] { "jake@example.invalid" }), CancellationToken.None);

        VerifyAdded(Times.Once());
    }

    [Fact]
    public async Task Remove_OfAnotherAdministrator_IsAllowed()
    {
        var controller = Controller(true, Me, "jake@example.invalid");

        await controller.RemoveAdministrator("Jake@example.invalid", CancellationToken.None);

        VerifyRemoved(Times.Once());
    }

    [Fact]
    public async Task Remove_OfTheCallerThemselves_IsRefused_WhileOthersRemain()
    {
        var controller = Controller(true, Me, "jake@example.invalid");

        await Assert.ThrowsAsync<DomainException>(() => controller.RemoveAdministrator(Me, CancellationToken.None));
        VerifyRemoved(Times.Never());
    }

    [Fact]
    public async Task Remove_OfTheLastAdministrator_PassesTheLockoutGuard_AndIsLeftToTheDomain()
    {
        // The lockout guard only cares that the caller is not cut off; an empty list does not do
        // that. Refusing to empty the list is AuthenticationSettings.RemoveAdmin's job, judged on the
        // freshly loaded row — see AuthenticationSettingsAdministratorsTests.
        var controller = Controller(true, Me);

        await controller.RemoveAdministrator(Me, CancellationToken.None);

        VerifyRemoved(Times.Once());
    }

    [Fact]
    public async Task Remove_OfTheCaller_IsAllowed_WhenSignInIsNotRequired()
    {
        var controller = Controller(false, Me, "jake@example.invalid");

        await controller.RemoveAdministrator(Me, CancellationToken.None);

        VerifyRemoved(Times.Once());
    }
}
