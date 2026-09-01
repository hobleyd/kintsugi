using Moq;
using Kintsugi.Application.AgentPackages.Queries.GetAgentPackageSourceStatus;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Tests.Application.AgentPackages;

public class GetAgentPackageSourceStatusQueryHandlerTests
{
    private readonly Mock<IAgentPackageSourceClient> _sourceClient = new();
    private readonly Mock<IAgentPackageRepository> _repository = new();

    public GetAgentPackageSourceStatusQueryHandlerTests()
    {
        _repository.Setup(r => r.GetLatestPerPlatformAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentPackage>());
    }

    private GetAgentPackageSourceStatusQueryHandler CreateHandler() =>
        new(_sourceClient.Object, _repository.Object, FakeGitHubSettings.Provider());

    private void SourceHas(params (string Platform, string Version)[] releases) =>
        _sourceClient.Setup(c => c.GetLatestReleasesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(releases
                .Select(r => new AgentPackageSourceRelease(r.Platform, r.Version, "f.tar.gz", "https://example/f.tar.gz", null))
                .ToList());

    private void PublishedHere(params (string Platform, string Version)[] packages) =>
        _repository.Setup(r => r.GetLatestPerPlatformAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(packages
                .Select(p => AgentPackage.Create(p.Platform, p.Version, "f.tar.gz", 1024, new string('a', 64), "sig", null))
                .ToList());

    [Fact]
    public async Task Handle_MarksAPlatformNewerWhenUpstreamHasABiggerVersion()
    {
        SourceHas(("macos", "0.6.0"));
        PublishedHere(("macos", "0.5.0"));

        var status = await CreateHandler().Handle(new GetAgentPackageSourceStatusQuery(), CancellationToken.None);

        var row = Assert.Single(status.Platforms);
        Assert.Equal("0.6.0", row.AvailableVersion);
        Assert.Equal("0.5.0", row.PublishedVersion);
        Assert.True(row.IsNewer);
        Assert.True(status.HasNewVersions);
    }

    [Fact]
    public async Task Handle_SameVersionOnBothSides_IsNotNewer()
    {
        SourceHas(("macos", "0.5.0"));
        PublishedHere(("macos", "0.5.0"));

        var status = await CreateHandler().Handle(new GetAgentPackageSourceStatusQuery(), CancellationToken.None);

        Assert.False(Assert.Single(status.Platforms).IsNewer);
        Assert.False(status.HasNewVersions);
    }

    [Fact]
    public async Task Handle_PlatformWithNothingPublishedHere_IsNewer()
    {
        SourceHas(("linux", "0.5.0"));

        var status = await CreateHandler().Handle(new GetAgentPackageSourceStatusQuery(), CancellationToken.None);

        var row = Assert.Single(status.Platforms);
        Assert.Null(row.PublishedVersion);
        Assert.True(row.IsNewer);
    }

    [Fact]
    public async Task Handle_UnreachableSource_ReportsTheReasonInsteadOfThrowing()
    {
        // This runs on every Clients page load, and the packages already published here are
        // installable whether or not GitHub is reachable — so the failure must arrive as data the
        // page can render beside the working downloads, not as an exception that replaces them.
        _sourceClient.Setup(c => c.GetLatestReleasesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Listing releases took longer than 10 seconds."));

        var status = await CreateHandler().Handle(new GetAgentPackageSourceStatusQuery(), CancellationToken.None);

        Assert.Equal("Listing releases took longer than 10 seconds.", status.UnavailableReason);
        Assert.Empty(status.Platforms);
        Assert.False(status.HasNewVersions);
        Assert.Equal("hobleyd/kintsugi", status.SourceDescription);
    }

    [Fact]
    public async Task Handle_CancellationStillPropagates()
    {
        // The catch-all above must not swallow the request having gone away — that would turn a
        // cancelled page load into a full upstream call charged against GitHub's rate limit.
        _sourceClient.Setup(c => c.GetLatestReleasesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateHandler().Handle(new GetAgentPackageSourceStatusQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_OrdersPlatformsForAStableRender()
    {
        SourceHas(("windows", "0.5.0"), ("macos", "0.5.0"), ("linux", "0.5.0"));

        var status = await CreateHandler().Handle(new GetAgentPackageSourceStatusQuery(), CancellationToken.None);

        Assert.Equal(new[] { "linux", "macos", "windows" }, status.Platforms.Select(p => p.Platform));
    }
}
