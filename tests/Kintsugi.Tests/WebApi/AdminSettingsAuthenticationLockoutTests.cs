using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Kintsugi.Application.Authentication;
using Kintsugi.Application.Authentication.Commands.UpdateAuthenticationSettings;
using Kintsugi.Application.Authentication.Queries.GetAuthenticationSettings;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;
using Kintsugi.WebApi.Controllers;

namespace Kintsugi.Tests.WebApi;

/// <summary>
/// The guard that stops an administrator saving an allowlist they are not on: the very next
/// request would be a 403 and nobody could repair the list from the UI. It must judge the list as it
/// will be stored — the stored one when the request sends null, with blank entries dropped.
/// </summary>
public class AdminSettingsAuthenticationLockoutTests
{
    private readonly Mock<ISender> _sender = new();

    private AdminSettingsController Controller(params Claim[] claims) =>
        Controller(new ClaimsIdentity(claims, "TestAuth"), Array.Empty<string>());

    private AdminSettingsController Controller(ClaimsIdentity identity, IReadOnlyList<string> storedAdmins) =>
        Controller(identity, AuthenticationSettingsDto.NotConfigured() with { IsEnabled = true, AdminEmails = storedAdmins });

    private AdminSettingsController Controller(ClaimsIdentity identity, AuthenticationSettingsDto stored)
    {
        _sender.Setup(s => s.Send(It.IsAny<UpdateAuthenticationSettingsCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthenticationSettingsDto.NotConfigured());
        _sender.Setup(s => s.Send(It.IsAny<GetAuthenticationSettingsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

        return new AdminSettingsController(
            _sender.Object, Mock.Of<IOptionsMonitorCache<OpenIdConnectOptions>>(), Mock.Of<IVantaSyncCoordinator>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    private static ClaimsIdentity SignedInAs(string email) =>
        new(new[] { new Claim("email", email), new Claim("email_verified", "true"), new Claim("hd", "example.invalid") }, "TestAuth");

    private static UpdateAuthenticationSettingsCommand Command(bool enabled, params string[] admins) =>
        new(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, null, enabled, admins);

    private static UpdateAuthenticationSettingsCommand KeepStoredList() =>
        new(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, null, true, null);

    private void VerifySaved(Times times) =>
        _sender.Verify(s => s.Send(It.IsAny<UpdateAuthenticationSettingsCommand>(), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task ListWithoutTheCaller_IsRefused_WhenSignInIsRequired()
    {
        var controller = Controller(new Claim("email", "me@example.invalid"), new Claim("email_verified", "true"), new Claim("hd", "example.invalid"));

        await Assert.ThrowsAsync<DomainException>(() =>
            controller.UpdateAuthentication(Command(true, "someone.else@example.invalid"), CancellationToken.None));
        VerifySaved(Times.Never());
    }

    [Fact]
    public async Task ListWithTheCaller_IsSaved_CaseInsensitively()
    {
        var controller = Controller(new Claim("email", "me@example.invalid"), new Claim("email_verified", "true"), new Claim("hd", "example.invalid"));

        await controller.UpdateAuthentication(Command(true, "ME@Example.invalid"), CancellationToken.None);

        VerifySaved(Times.Once());
    }

    [Fact]
    public async Task EmptyList_IsSaved()
    {
        var controller = Controller(new Claim("email", "me@example.invalid"), new Claim("email_verified", "true"), new Claim("hd", "example.invalid"));

        await controller.UpdateAuthentication(Command(true), CancellationToken.None);

        VerifySaved(Times.Once());
    }

    [Fact]
    public async Task ListWithoutTheCaller_IsSaved_WhenSignInIsNotRequired()
    {
        var controller = Controller();

        await controller.UpdateAuthentication(Command(false, "someone.else@example.invalid"), CancellationToken.None);

        VerifySaved(Times.Once());
    }

    // A null list means "keep the stored list", which the Settings screen sends when it does not
    // touch the field. The first version skipped the check for it, so turning sign-in on over a
    // stored list the caller was not on locked everyone out.
    [Fact]
    public async Task NullList_IsRefused_WhenTheStoredListLacksTheCaller()
    {
        var controller = Controller(SignedInAs("me@example.invalid"), new[] { "someone.else@example.invalid" });

        await Assert.ThrowsAsync<DomainException>(() => controller.UpdateAuthentication(KeepStoredList(), CancellationToken.None));
        VerifySaved(Times.Never());
    }

    [Fact]
    public async Task NullList_IsSaved_WhenTheStoredListHasTheCaller()
    {
        var controller = Controller(SignedInAs("me@example.invalid"), new[] { "me@example.invalid" });

        await controller.UpdateAuthentication(KeepStoredList(), CancellationToken.None);

        VerifySaved(Times.Once());
    }

    // A null entry once compared equal to a caller with no email; the entity then dropped it and
    // stored only the other address, locking that caller out.
    [Fact]
    public async Task NullEntry_DoesNotMatchACallerWithNoEmail()
    {
        var controller = Controller(new Claim("name", "No Email"));
        var command = new UpdateAuthenticationSettingsCommand(
            AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, null, true, new string[] { null!, "someone.else@example.invalid" });

        await Assert.ThrowsAsync<DomainException>(() => controller.UpdateAuthentication(command, CancellationToken.None));
        VerifySaved(Times.Never());
    }

    [Fact]
    public async Task ListOfOnlyBlanks_IsSaved_BecauseItIsStoredAsEmpty()
    {
        var controller = Controller(new Claim("name", "No Email"));

        await controller.UpdateAuthentication(Command(true, "", "  "), CancellationToken.None);

        VerifySaved(Times.Once());
    }

    // First run: while sign-in is off nobody is signed in, so nobody has an email to be on a list
    // with. Enabling sign-in with a list is refused with the two-step instructions instead.
    [Fact]
    public async Task NotSignedIn_EnablingWithAList_IsRefusedWithTheTwoStepMessage()
    {
        var controller = Controller(new ClaimsIdentity(), Array.Empty<string>());

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            controller.UpdateAuthentication(Command(true, "me@example.invalid"), CancellationToken.None));
        Assert.Contains("two saves", ex.Message);
        VerifySaved(Times.Never());
    }

    [Fact]
    public async Task NotSignedIn_EnablingWithAnEmptyList_IsSaved()
    {
        var controller = Controller(new ClaimsIdentity(), Array.Empty<string>());

        await controller.UpdateAuthentication(Command(true), CancellationToken.None);

        VerifySaved(Times.Once());
    }

    // The caller's session proves who they are to the stored identity source, not the one being
    // saved: switching provider (or hosted domain, tenant, issuer) under a list passes the email
    // check and then locks everyone out once cookies expire.
    private static AuthenticationSettingsDto StoredGoogle(string? hostedDomain, params string[] admins) =>
        AuthenticationSettingsDto.NotConfigured() with { IsEnabled = true, HostedDomain = hostedDomain, AdminEmails = admins };

    [Fact]
    public async Task SwitchingProvider_UnderAList_IsRefused()
    {
        var controller = Controller(SignedInAs("me@example.invalid"), StoredGoogle("example.invalid", "me@example.invalid"));
        var command = new UpdateAuthenticationSettingsCommand(
            AuthProvider.GenericOidc, "client-id", "secret", "https://issuer.example.invalid", null, null, true, new[] { "me@example.invalid" });

        var ex = await Assert.ThrowsAsync<DomainException>(() => controller.UpdateAuthentication(command, CancellationToken.None));
        Assert.Contains("Clear the list first", ex.Message);
        VerifySaved(Times.Never());
    }

    [Fact]
    public async Task ChangingHostedDomain_UnderTheStoredList_IsRefused()
    {
        var controller = Controller(SignedInAs("me@example.invalid"), StoredGoogle("example.invalid", "me@example.invalid"));
        var command = new UpdateAuthenticationSettingsCommand(
            AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, "other.invalid", true, null);

        await Assert.ThrowsAsync<DomainException>(() => controller.UpdateAuthentication(command, CancellationToken.None));
        VerifySaved(Times.Never());
    }

    [Fact]
    public async Task SameIdentitySource_DifferingOnlyInCaseAndSpace_IsSaved()
    {
        var controller = Controller(SignedInAs("me@example.invalid"), StoredGoogle("example.invalid", "me@example.invalid"));
        var command = new UpdateAuthenticationSettingsCommand(
            AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, " Example.invalid ", true, null);

        await controller.UpdateAuthentication(command, CancellationToken.None);

        VerifySaved(Times.Once());
    }

    [Fact]
    public async Task SwitchingProvider_WithAnEmptyList_IsSaved()
    {
        var controller = Controller(SignedInAs("me@example.invalid"), StoredGoogle("example.invalid"));
        var command = new UpdateAuthenticationSettingsCommand(
            AuthProvider.GenericOidc, "client-id", "secret", "https://issuer.example.invalid", null, null, true, Array.Empty<string>());

        await controller.UpdateAuthentication(command, CancellationToken.None);

        VerifySaved(Times.Once());
    }
}
