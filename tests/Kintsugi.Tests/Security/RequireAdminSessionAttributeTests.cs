using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.Tests.Security;

/// <summary>
/// Covers the gate that closes a hole neither existing mechanism did: nginx's client-certificate
/// regex is an exact match, so it never covers a browser-driven sub-route, and <c>Program.cs</c>
/// exempts all of <c>/api</c> from the sign-in gate on the reasoning that agents use mutual TLS
/// rather than cookies. Each is right alone; together they left routes that change what agents
/// execute reachable by anyone.
///
/// The semantics deliberately mirror <c>Program.cs</c>'s own gate rather than inventing a second
/// shape that could drift from it, so these tests are as much about that agreement as about the
/// filter.
/// </summary>
public class RequireAdminSessionAttributeTests
{
    private readonly Mock<IAuthenticationSettingsRepository> _settings = new();

    private static AuthenticationSettings Settings(bool isEnabled) =>
        AuthenticationSettings.Create(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, null, isEnabled);

    private async Task<(ActionExecutingContext Context, bool RanAction)> InvokeAsync(bool authenticated)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_settings.Object);

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        httpContext.User = authenticated
            ? new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "admin@example.invalid") }, "TestAuth"))
            // An identity with no authentication type is unauthenticated, which is what an
            // anonymous request actually looks like here.
            : new ClaimsPrincipal(new ClaimsIdentity());

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(
            actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());

        var ranAction = false;
        await new RequireAdminSessionAttribute().OnActionExecutionAsync(context, () =>
        {
            ranAction = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller: new object()));
        });

        return (context, ranAction);
    }

    [Fact]
    public async Task WhenAuthenticationIsEnabledAndTheCallerIsAnonymous_Rejects()
    {
        _settings.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Settings(isEnabled: true));

        var (context, ranAction) = await InvokeAsync(authenticated: false);

        Assert.False(ranAction);
        Assert.Equal(StatusCodes.Status401Unauthorized, (context.Result as ObjectResult)?.StatusCode);
    }

    [Fact]
    public async Task WhenAuthenticationIsEnabledAndTheCallerIsSignedIn_Allows()
    {
        _settings.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Settings(isEnabled: true));

        var (context, ranAction) = await InvokeAsync(authenticated: true);

        Assert.True(ranAction);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task WhenAuthenticationIsDisabled_Allows()
    {
        _settings.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Settings(isEnabled: false));

        var (context, ranAction) = await InvokeAsync(authenticated: false);

        // The admin has deliberately chosen to run the site open, and these routes are then no more
        // exposed than the pages that do the same thing. Matching Program.cs rather than being
        // stricter than it is the point — two gates that disagree is how one of them gets "fixed".
        Assert.True(ranAction);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task WhenNothingHasBeenConfiguredYet_Allows()
    {
        _settings.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((AuthenticationSettings?)null);

        var (context, ranAction) = await InvokeAsync(authenticated: false);

        // A fresh deploy, which Program.cs redirects the whole browser UI to
        // /settings/authentication for. Failing closed here would leave no way to reach a first-run
        // state that has no enrolled agents to attack yet.
        Assert.True(ranAction);
        Assert.Null(context.Result);
    }
}
