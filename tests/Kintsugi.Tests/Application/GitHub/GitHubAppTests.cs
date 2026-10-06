using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.GitHub;
using Kintsugi.Application.GitHub.Commands.CompleteGitHubAppManifest;
using Kintsugi.Application.GitHub.Commands.RecordGitHubAppInstallation;
using Kintsugi.Application.GitHub.Queries.GetGitHubSettings;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;
using Kintsugi.Infrastructure.GitHubApp;
using Kintsugi.Infrastructure.ScriptApproval;
using MediatR;

namespace Kintsugi.Tests.Application.GitHub;

/// <summary>A real RSA key in GitHub's PKCS#1 PEM shape, generated once per test run.</summary>
internal static class TestAppKey
{
    public static readonly RSA Rsa = RSA.Create(2048);
    public static readonly string Pem = Rsa.ExportRSAPrivateKeyPem();
}

public class GitHubSettingsAppEntityTests
{
    [Fact]
    public void ConnectGitHubApp_ForgetsAnEarlierInstallation()
    {
        var settings = GitHubSettings.Create(null, null, null, null);
        settings.ConnectGitHubApp(1, "first-app", "acme", TestAppKey.Pem);
        settings.RecordGitHubAppInstallation(100);

        settings.ConnectGitHubApp(2, "second-app", "acme", TestAppKey.Pem);

        // An installation belongs to one App; keeping it would have the new App mint tokens for an
        // installation it does not own.
        Assert.Null(settings.GitHubAppInstallationId);
        Assert.True(settings.HasGitHubApp);
        Assert.False(settings.IsGitHubAppInstalled);
    }

    [Fact]
    public void RecordGitHubAppInstallation_WithoutAnApp_IsRefused()
    {
        var settings = GitHubSettings.Create(null, null, null, null);

        Assert.Throws<DomainException>(() => settings.RecordGitHubAppInstallation(100));
    }

    [Fact]
    public void ConnectGitHubApp_RefusesSomethingThatIsNotAPrivateKey()
    {
        var settings = GitHubSettings.Create(null, null, null, null);

        Assert.Throws<DomainException>(() => settings.ConnectGitHubApp(1, "app", "acme", "not a key"));
    }

    [Fact]
    public void DisconnectGitHubApp_LeavesTheStoredTokensAlone()
    {
        var settings = GitHubSettings.Create("api", null, null, "approval");
        settings.ConnectGitHubApp(1, "app", "acme", TestAppKey.Pem);
        settings.RecordGitHubAppInstallation(100);

        settings.DisconnectGitHubApp();

        // Disconnecting hands control back to the tokens, so it must not take them with it.
        Assert.False(settings.HasGitHubApp);
        Assert.Null(settings.GitHubAppPrivateKey);
        Assert.Equal("api", settings.ApiToken);
        Assert.Equal("approval", settings.ScriptApprovalToken);
    }
}

public class GitHubAppJwtTests
{
    [Fact]
    public void Create_SignsWithTheAppKey_AndStaysInsideGitHubsTenMinuteLimit()
    {
        var now = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

        var jwt = GitHubAppJwt.Create(12345, TestAppKey.Pem, now);

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        Assert.True(TestAppKey.Rsa.VerifyData(
            signed, FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());

        using var payload = JsonDocument.Parse(FromBase64Url(parts[1]));
        var iat = payload.RootElement.GetProperty("iat").GetInt64();
        var exp = payload.RootElement.GetProperty("exp").GetInt64();
        Assert.Equal("12345", payload.RootElement.GetProperty("iss").GetString());
        // GitHub rejects a lifetime over ten minutes, and an iat in its future.
        Assert.True(exp - iat <= 600);
        Assert.True(iat < now.ToUnixTimeSeconds());
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}

public class GitHubAppManifestTests
{
    [Fact]
    public void Build_AsksForOnlyWhatPublishingNeeds_AndNoWebhook()
    {
        var json = GitHubAppManifest.Build(
            "Kintsugi test", "https://kintsugi.example.com", "https://kintsugi.example.com/cb", "https://kintsugi.example.com/setup");

        using var manifest = JsonDocument.Parse(json);
        var root = manifest.RootElement;
        var permissions = root.GetProperty("default_permissions");

        Assert.Equal("write", permissions.GetProperty("contents").GetString());
        Assert.Equal("write", permissions.GetProperty("pull_requests").GetString());
        Assert.Equal("read", permissions.GetProperty("metadata").GetString());
        Assert.Equal(3, permissions.EnumerateObject().Count());
        Assert.False(root.GetProperty("hook_attributes").GetProperty("active").GetBoolean());
        Assert.False(root.GetProperty("public").GetBoolean());
        Assert.Equal("https://kintsugi.example.com/cb", root.GetProperty("redirect_url").GetString());
        Assert.Equal("https://kintsugi.example.com/setup", root.GetProperty("setup_url").GetString());
    }

    [Fact]
    public void DefaultName_FitsGitHubsLimit()
    {
        var name = GitHubAppManifest.DefaultName("a-very-long-hostname.subdomain.example.com");

        Assert.True(name.Length <= GitHubAppManifest.MaxNameLength);
        Assert.StartsWith("Kintsugi ", name);
    }

    [Theory]
    [InlineData("acme", "https://github.com/organizations/acme/settings/apps/new?state=s")]
    [InlineData(null, "https://github.com/settings/apps/new?state=s")]
    [InlineData("  ", "https://github.com/settings/apps/new?state=s")]
    public void CreateUrl_TargetsTheOrganisationWhenOneIsGiven(string? organization, string expected) =>
        Assert.Equal(expected, GitHubAppManifest.CreateUrl(organization, "s"));
}

/// <summary>
/// Records each installation-token request and answers with a token named after the request's
/// repository restriction, so a test can tell which scope it was handed.
/// </summary>
internal sealed class FakeTokenEndpoint : HttpMessageHandler
{
    public List<(string Url, string Body)> Requests { get; } = new();
    public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri!.ToString(), body));

        if (Status != HttpStatusCode.Created)
        {
            return new HttpResponseMessage(Status) { Content = new StringContent("{\"message\":\"nope\"}") };
        }

        using var parsed = JsonDocument.Parse(body);
        var token = parsed.RootElement.TryGetProperty("repositories", out var repos)
            ? "write-token-for-" + string.Join(",", repos.EnumerateArray().Select(r => r.GetString()))
            : "read-token";

        return new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                token,
                expires_at = DateTimeOffset.UtcNow.AddHours(1),
            })),
        };
    }

    public IHttpClientFactory Factory()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(this, disposeHandler: false));
        return factory.Object;
    }
}

public class GitHubAppTokenProviderTests
{
    [Fact]
    public async Task GetAsync_CachesPerScope()
    {
        var endpoint = new FakeTokenEndpoint();
        var provider = new GitHubAppTokenProvider(endpoint.Factory());

        var first = await provider.GetAsync(1, TestAppKey.Pem, 100, GitHubTokenScope.ReadOnly, CancellationToken.None);
        var again = await provider.GetAsync(1, TestAppKey.Pem, 100, GitHubTokenScope.ReadOnly, CancellationToken.None);
        var write = await provider.GetAsync(1, TestAppKey.Pem, 100, GitHubTokenScope.ApprovalWrite("scripts"), CancellationToken.None);

        Assert.Equal(first, again);
        Assert.NotEqual(first, write);
        // One mint per scope: the second read came from the cache.
        Assert.Equal(2, endpoint.Requests.Count);
        Assert.All(endpoint.Requests, r => Assert.Equal("https://api.github.com/app/installations/100/access_tokens", r.Url));
    }

    [Fact]
    public async Task ReadOnlyScope_NeverAsksForWrite()
    {
        var endpoint = new FakeTokenEndpoint();
        var provider = new GitHubAppTokenProvider(endpoint.Factory());

        await provider.GetAsync(1, TestAppKey.Pem, 100, GitHubTokenScope.ReadOnly, CancellationToken.None);

        // This token goes to the AI research and agent-package clients too, which must never hold
        // write access — see .claude/rules/script-approval-repo.md.
        using var body = JsonDocument.Parse(endpoint.Requests.Single().Body);
        var permissions = body.RootElement.GetProperty("permissions");
        Assert.All(permissions.EnumerateObject(), p => Assert.Equal("read", p.Value.GetString()));
        Assert.False(body.RootElement.TryGetProperty("repositories", out _));
    }

    [Fact]
    public async Task GetAsync_WhenGitHubRefuses_Throws()
    {
        var endpoint = new FakeTokenEndpoint { Status = HttpStatusCode.UnprocessableEntity };
        var provider = new GitHubAppTokenProvider(endpoint.Factory());

        await Assert.ThrowsAsync<ExternalServiceException>(() =>
            provider.GetAsync(1, TestAppKey.Pem, 100, GitHubTokenScope.ApprovalWrite("elsewhere"), CancellationToken.None));
    }
}

public class GitHubSettingsProviderAppModeTests
{
    private readonly Mock<IGitHubSettingsRepository> _repository = new();
    private readonly FakeTokenEndpoint _endpoint = new();

    private GitHubSettingsProvider CreateProvider() => new(
        _repository.Object, new GitHubAppTokenProvider(_endpoint.Factory()), NullLogger<GitHubSettingsProvider>.Instance);

    private GitHubSettings Stored(bool installed)
    {
        var settings = GitHubSettings.Create("personal-api", "acme/builds", "acme/scripts", "personal-approval");
        settings.ConnectGitHubApp(1, "kintsugi-acme", "acme", TestAppKey.Pem);
        if (installed)
        {
            settings.RecordGitHubAppInstallation(100);
        }

        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        return settings;
    }

    [Fact]
    public async Task AnInstalledApp_ReplacesBothTokens_WithNarrowedInstallationTokens()
    {
        Stored(installed: true);

        var snapshot = await CreateProvider().GetAsync(CancellationToken.None);

        Assert.Equal("read-token", snapshot.ApiToken);
        // Restricted to the approval repository by name; the owner is implied by the installation.
        Assert.Equal("write-token-for-scripts", snapshot.ScriptApprovalToken);
        Assert.True(snapshot.CanPublishScriptApprovals);
    }

    [Fact]
    public async Task AnAppNotYetInstalled_LeavesTheStoredTokensInCharge()
    {
        Stored(installed: false);

        var snapshot = await CreateProvider().GetAsync(CancellationToken.None);

        Assert.Equal("personal-api", snapshot.ApiToken);
        Assert.Equal("personal-approval", snapshot.ScriptApprovalToken);
        Assert.Empty(_endpoint.Requests);
    }

    [Fact]
    public async Task AnInstalledAppThatCannotMint_GivesNoTokens_RatherThanFallingBackToPersonalOnes()
    {
        Stored(installed: true);
        _endpoint.Status = HttpStatusCode.Unauthorized;

        var snapshot = await CreateProvider().GetAsync(CancellationToken.None);

        // A failing App must be seen to fail, not papered over by a personal token somebody forgot
        // was still saved — and must not throw, or the settings page needed to fix it goes down too.
        Assert.Null(snapshot.ApiToken);
        Assert.Null(snapshot.ScriptApprovalToken);
        Assert.False(snapshot.CanPublishScriptApprovals);
    }
}

public class GitHubAppCommandTests
{
    private readonly Mock<IGitHubSettingsRepository> _repository = new();
    private readonly Mock<IGitHubAppClient> _client = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISender> _sender = new();

    [Fact]
    public async Task CompleteManifest_StoresTheAppOnAFreshDeployment()
    {
        GitHubSettings? added = null;
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((GitHubSettings?)null);
        _repository.Setup(r => r.AddAsync(It.IsAny<GitHubSettings>(), It.IsAny<CancellationToken>()))
            .Callback<GitHubSettings, CancellationToken>((s, _) => added = s)
            .Returns(Task.CompletedTask);
        _client.Setup(c => c.ConvertManifestAsync("the-code", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubAppCredentials(7, "kintsugi-acme", "acme", TestAppKey.Pem));

        var handler = new CompleteGitHubAppManifestCommandHandler(_repository.Object, _client.Object, _unitOfWork.Object);
        var slug = await handler.Handle(new CompleteGitHubAppManifestCommand(" the-code "), CancellationToken.None);

        Assert.Equal("kintsugi-acme", slug);
        Assert.NotNull(added);
        Assert.Equal(7, added!.GitHubAppId);
        Assert.False(added.IsGitHubAppInstalled);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordInstallation_WhenGitHubDisownsIt_RecordsNothing()
    {
        var settings = GitHubSettings.Create(null, null, null, null);
        settings.ConnectGitHubApp(7, "kintsugi-acme", "acme", TestAppKey.Pem);
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        _client.Setup(c => c.GetInstallationAccountAsync(7, TestAppKey.Pem, 999, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExternalServiceException("not ours"));

        var handler = new RecordGitHubAppInstallationCommandHandler(
            _repository.Object, _client.Object, _sender.Object, _unitOfWork.Object);

        // The id came from a query string; only GitHub's answer, asked as the App, makes it ours.
        await Assert.ThrowsAsync<ExternalServiceException>(() =>
            handler.Handle(new RecordGitHubAppInstallationCommand(999), CancellationToken.None));
        Assert.Null(settings.GitHubAppInstallationId);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordInstallation_WhenGitHubConfirmsIt_Records()
    {
        var settings = GitHubSettings.Create(null, null, null, null);
        settings.ConnectGitHubApp(7, "kintsugi-acme", "acme", TestAppKey.Pem);
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        _client.Setup(c => c.GetInstallationAccountAsync(7, TestAppKey.Pem, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync("acme");
        _sender.Setup(s => s.Send(It.IsAny<GetGitHubSettingsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubSettingsDto("acme/builds", false, "acme/scripts", false, false, false, "kintsugi-acme", "acme", true));

        var handler = new RecordGitHubAppInstallationCommandHandler(
            _repository.Object, _client.Object, _sender.Object, _unitOfWork.Object);
        await handler.Handle(new RecordGitHubAppInstallationCommand(100), CancellationToken.None);

        Assert.Equal(100, settings.GitHubAppInstallationId);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
