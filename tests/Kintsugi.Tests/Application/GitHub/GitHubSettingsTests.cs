using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.GitHub.Commands.UpdateGitHubSettings;
using Kintsugi.Application.GitHub.Queries.GetGitHubSettings;
using Kintsugi.Domain.Entities;
using Kintsugi.Infrastructure.ScriptApproval;

namespace Kintsugi.Tests.Application.GitHub;

public class GitHubSettingsEntityTests
{
    [Fact]
    public void Update_WithABlankToken_KeepsTheStoredOne()
    {
        var settings = GitHubSettings.Create("stored-api", "acme/builds", "acme/scripts", "stored-approval");

        settings.Update(apiToken: "  ", agentPackageRepository: "acme/builds", scriptApprovalRepository: "acme/scripts", scriptApprovalToken: null);

        // The page never receives the real token, so it cannot send it back unchanged — blank has to
        // mean "keep" or every save would wipe both credentials.
        Assert.Equal("stored-api", settings.ApiToken);
        Assert.Equal("stored-approval", settings.ScriptApprovalToken);
    }

    [Fact]
    public void Update_WithABlankRepository_ClearsItSoTheDefaultApplies()
    {
        var settings = GitHubSettings.Create(null, "acme/builds", "acme/scripts", null);

        settings.Update(null, agentPackageRepository: "", scriptApprovalRepository: null, scriptApprovalToken: null);

        // A repository *is* round-tripped to the page, so unlike a token, blank genuinely means
        // "unset it" rather than "keep".
        Assert.Null(settings.AgentPackageRepository);
        Assert.Null(settings.ScriptApprovalRepository);
    }

    [Fact]
    public void Update_TrimsATrailingSlashOffARepository()
    {
        var settings = GitHubSettings.Create(null, " acme/builds/ ", null, null);

        // "owner/repo/" pasted from a browser address bar would otherwise build every API URL with a
        // double slash and 404 on all of them.
        Assert.Equal("acme/builds", settings.AgentPackageRepository);
    }

    [Fact]
    public void ClearApiToken_RemovesItWithoutTouchingTheOther()
    {
        var settings = GitHubSettings.Create("stored-api", null, null, "stored-approval");

        settings.ClearApiToken();

        Assert.Null(settings.ApiToken);
        Assert.Equal("stored-approval", settings.ScriptApprovalToken);
    }
}

public class GitHubSettingsProviderTests
{
    private readonly Mock<IGitHubSettingsRepository> _repository = new();

    private GitHubSettingsProvider CreateProvider() => new(_repository.Object);

    [Fact]
    public async Task GetAsync_WhenNothingIsStored_FallsBackToTheDefaultRepository()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((GitHubSettings?)null);

        var snapshot = await CreateProvider().GetAsync(CancellationToken.None);

        // Resolved at read time rather than written into the row, so the default lives in one place.
        Assert.Equal(GitHubSettingsProvider.DefaultRepository, snapshot.AgentPackageRepository);
        Assert.Equal(GitHubSettingsProvider.DefaultRepository, snapshot.ScriptApprovalRepository);
        Assert.Null(snapshot.ApiToken);
        Assert.False(snapshot.CanPublishScriptApprovals);
    }

    [Fact]
    public async Task GetAsync_PrefersStoredValues()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(GitHubSettings.Create("api", "acme/builds", "acme/scripts", "approval"));

        var snapshot = await CreateProvider().GetAsync(CancellationToken.None);

        Assert.Equal("acme/builds", snapshot.AgentPackageRepository);
        Assert.Equal("acme/scripts", snapshot.ScriptApprovalRepository);
        Assert.Equal("api", snapshot.ApiToken);
        Assert.True(snapshot.CanPublishScriptApprovals);
    }

    [Fact]
    public async Task GetAsync_ReflectsAnEditImmediately()
    {
        var stored = GitHubSettings.Create(null, null, "acme/scripts", null);
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(stored);
        var provider = CreateProvider();

        Assert.False((await provider.GetAsync(CancellationToken.None)).CanPublishScriptApprovals);

        stored.Update(null, null, "acme/scripts", "a-token-saved-on-the-settings-page");

        // Nothing is cached, deliberately: the whole reason these clients read per call instead of
        // capturing configuration is that a settings-page edit has to take effect on the next request.
        Assert.True((await provider.GetAsync(CancellationToken.None)).CanPublishScriptApprovals);
    }
}

public class UpdateGitHubSettingsCommandValidatorTests
{
    private readonly UpdateGitHubSettingsCommandValidator _validator = new();

    private static UpdateGitHubSettingsCommand Command(string? agentPackageRepository) =>
        new(agentPackageRepository, null, null, false, null, false);

    [Theory]
    [InlineData("hobleyd/kintsugi")]
    [InlineData("acme-corp/internal.scripts")]
    public void Accepts_OwnerSlashName(string repository) =>
        Assert.True(_validator.Validate(Command(repository)).IsValid);

    [Theory]
    [InlineData("https://github.com/hobleyd/kintsugi")]
    [InlineData("kintsugi")]
    [InlineData("owner/repo/extra")]
    public void Rejects_AnythingElse(string repository)
    {
        // Every GitHub API URL interpolates this straight in, so a full URL produces nonsense that
        // 404s with no hint as to why.
        Assert.False(_validator.Validate(Command(repository)).IsValid);
    }

    [Fact]
    public void Accepts_Blank_WhichMeansUseTheDefault() =>
        Assert.True(_validator.Validate(Command("")).IsValid);
}
