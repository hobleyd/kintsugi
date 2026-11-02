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
/// The status <see cref="RequireAdminSessionAttribute"/> answers with when it refuses: 401 means
/// "sign in again" and 403 means "signed in, but not on the administrators list". The admin UI acts
/// on the difference, so swapping them would send a listed-out user round a sign-in loop.
/// </summary>
public class RequireAdminSessionStatusTests
{
    private static async Task<(int? Status, bool NextCalled)> Run(ClaimsIdentity identity, params string[] admins)
    {
        var settings = AuthenticationSettings.Create(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, null, true, admins);
        var repository = new Mock<IAuthenticationSettingsRepository>();
        repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            RequestServices = new ServiceCollection().AddSingleton(repository.Object).BuildServiceProvider(),
        };
        var context = new ActionExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());

        var nextCalled = false;
        await new RequireAdminSessionAttribute().OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), new object()));
        });

        return ((context.Result as ObjectResult)?.StatusCode, nextCalled);
    }

    [Fact]
    public async Task NotSignedIn_Is401()
    {
        var (status, next) = await Run(new ClaimsIdentity(), "admin@example.invalid");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(next);
    }

    [Fact]
    public async Task SignedInButNotListed_Is403()
    {
        var (status, next) = await Run(new ClaimsIdentity(new[] { new Claim("email", "other@example.invalid"), new Claim("email_verified", "true"), new Claim("hd", "example.invalid") }, "TestAuth"), "admin@example.invalid");

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(next);
    }

    [Fact]
    public async Task Listed_PassesThrough()
    {
        var (status, next) = await Run(new ClaimsIdentity(new[] { new Claim("email", "admin@example.invalid"), new Claim("email_verified", "true"), new Claim("hd", "example.invalid") }, "TestAuth"), "admin@example.invalid");

        Assert.Null(status);
        Assert.True(next);
    }
}
