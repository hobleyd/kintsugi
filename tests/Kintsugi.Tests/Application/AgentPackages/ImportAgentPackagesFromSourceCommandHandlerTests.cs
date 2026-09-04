using MediatR;
using Moq;
using Kintsugi.Application.AgentPackages;
using Kintsugi.Application.AgentPackages.Commands.ImportAgentPackagesFromSource;
using Kintsugi.Application.AgentPackages.Commands.PublishAgentPackage;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Tests.Application.AgentPackages;

public class ImportAgentPackagesFromSourceCommandHandlerTests
{
    private const string ApiBaseUrl = "https://patch.internal:8443";

    private readonly Mock<IAgentPackageSourceClient> _sourceClient = new();
    private readonly Mock<IAgentPackageArchiveRewriter> _archiveRewriter = new();
    private readonly Mock<IAgentPackageRepository> _repository = new();
    private readonly Mock<ISender> _sender = new();

    public ImportAgentPackagesFromSourceCommandHandlerTests()
    {
        _sourceClient.Setup(c => c.DownloadAsync(It.IsAny<AgentPackageSourceRelease>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 1, 2, 3 }));
        _archiveRewriter.Setup(r => r.WithApiBaseUrl(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 4, 5, 6 }));
        _repository.Setup(r => r.GetByPlatformAndVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentPackage?)null);
        _sender.Setup(s => s.Send(It.IsAny<PublishAgentPackageCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PublishAgentPackageCommand c, CancellationToken _) => Published(c.Platform, c.Version));
    }

    private ImportAgentPackagesFromSourceCommandHandler CreateHandler() =>
        new(_sourceClient.Object, _archiveRewriter.Object, _repository.Object, _sender.Object);

    private static AgentPackageDto Published(string platform, string version) =>
        new(platform, version, $"kintsugi-agent-{platform}-{version}.tar.gz", 1024, new string('a', 64), "sig", null, DateTimeOffset.UtcNow);

    private static AgentPackageSourceRelease Release(string platform = "macos", string version = "0.5.0", string? notes = "Build notes.") =>
        new(platform, version, $"kintsugi-agent-{platform}-{version}.tar.gz",
            $"https://github.com/hobleyd/kintsugi/releases/download/{platform}-agent-v{version}/kintsugi-agent-{platform}-{version}.tar.gz",
            notes);

    private void SourceHas(params AgentPackageSourceRelease[] releases) =>
        _sourceClient.Setup(c => c.GetReleasesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(releases);

    [Fact]
    public async Task Handle_DownloadsRewritesAndPublishesEachPlatform()
    {
        SourceHas(Release("macos"), Release("linux"), Release("windows"));

        var results = await CreateHandler().Handle(new ImportAgentPackagesFromSourceCommand(ApiBaseUrl), CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(AgentPackageImportOutcome.Imported, r.Outcome));
        _sender.Verify(s => s.Send(It.IsAny<PublishAgentPackageCommand>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Handle_BakesTheServersOwnAddressIntoEachArchive()
    {
        // The whole point of importing rather than downloading straight off GitHub: the upstream
        // archive carries the kintsugi.example.com placeholder, and it is rewritten here — at
        // import time, so the stored bytes and the checksum signed over them already describe this
        // server. See IAgentPackageArchiveRewriter.
        SourceHas(Release("macos"));

        await CreateHandler().Handle(new ImportAgentPackagesFromSourceCommand(ApiBaseUrl), CancellationToken.None);

        _archiveRewriter.Verify(
            r => r.WithApiBaseUrl(It.IsAny<Stream>(), ApiBaseUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_PublishesTheRewrittenStream_NotTheDownloadedOne()
    {
        SourceHas(Release("macos"));

        // Read while the command is in flight, not after: the handler owns both streams and
        // disposes them as soon as the publish returns, so a copy taken afterwards would only
        // prove that the handler cleans up.
        byte[]? publishedContent = null;
        _sender.Setup(s => s.Send(It.IsAny<PublishAgentPackageCommand>(), It.IsAny<CancellationToken>()))
            .Callback((object c, CancellationToken _) =>
            {
                var buffer = new MemoryStream();
                ((PublishAgentPackageCommand)c).Content.CopyTo(buffer);
                publishedContent = buffer.ToArray();
            })
            .ReturnsAsync(Published("macos", "0.5.0"));

        await CreateHandler().Handle(new ImportAgentPackagesFromSourceCommand(ApiBaseUrl), CancellationToken.None);

        Assert.Equal(new byte[] { 4, 5, 6 }, publishedContent);
    }

    [Fact]
    public async Task Handle_AlreadyPublishedVersion_IsSkippedWithoutDownloading()
    {
        // Checked before downloading rather than left to PublishAgentPackageCommandHandler's own
        // idempotency: those bytes are only identical while the server's address is unchanged, so
        // a server that moved would otherwise fail the publish outright.
        SourceHas(Release("macos", "0.5.0"));
        _repository.Setup(r => r.GetByPlatformAndVersionAsync("macos", "0.5.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentPackage.Create("macos", "0.5.0", "f.tar.gz", 1024, new string('a', 64), "sig", null));

        var result = Assert.Single(await CreateHandler().Handle(new ImportAgentPackagesFromSourceCommand(ApiBaseUrl), CancellationToken.None));

        Assert.Equal(AgentPackageImportOutcome.AlreadyPublished, result.Outcome);
        _sourceClient.Verify(c => c.DownloadAsync(It.IsAny<AgentPackageSourceRelease>(), It.IsAny<CancellationToken>()), Times.Never);
        _sender.Verify(s => s.Send(It.IsAny<PublishAgentPackageCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_OnePlatformFailing_StillImportsTheOthers()
    {
        // A fleet that got two of three agents refreshed is strictly better off than one that got
        // none, and the failure is still reported rather than swallowed.
        SourceHas(Release("macos"), Release("linux"), Release("windows"));
        _sourceClient.Setup(c => c.DownloadAsync(It.Is<AgentPackageSourceRelease>(r => r.Platform == "linux"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("404 while downloading the asset"));

        var results = await CreateHandler().Handle(new ImportAgentPackagesFromSourceCommand(ApiBaseUrl), CancellationToken.None);

        var failed = Assert.Single(results, r => r.Outcome == AgentPackageImportOutcome.Failed);
        Assert.Equal("linux", failed.Platform);
        Assert.Contains("404", failed.Message);
        Assert.Equal(2, results.Count(r => r.Outcome == AgentPackageImportOutcome.Imported));
    }

    [Fact]
    public async Task Handle_CarriesTheGitHubReleaseBodyThroughAsReleaseNotes()
    {
        SourceHas(Release(notes: "Fixes the snap list parser."));
        PublishAgentPackageCommand? published = null;
        _sender.Setup(s => s.Send(It.IsAny<PublishAgentPackageCommand>(), It.IsAny<CancellationToken>()))
            .Callback((object c, CancellationToken _) => published = (PublishAgentPackageCommand)c)
            .ReturnsAsync(Published("macos", "0.5.0"));

        await CreateHandler().Handle(new ImportAgentPackagesFromSourceCommand(ApiBaseUrl), CancellationToken.None);

        Assert.Equal("Fixes the snap list parser.", published!.ReleaseNotes);
    }

    [Fact]
    public async Task Handle_OverlongReleaseBody_IsTruncatedRatherThanFailingValidation()
    {
        // PublishAgentPackageCommandValidator caps release notes at 2000 characters, and a GitHub
        // release body has no length limit worth relying on. Losing the tail of a description
        // beats failing the whole import on a validation error.
        SourceHas(Release(notes: new string('x', 5000)));
        PublishAgentPackageCommand? published = null;
        _sender.Setup(s => s.Send(It.IsAny<PublishAgentPackageCommand>(), It.IsAny<CancellationToken>()))
            .Callback((object c, CancellationToken _) => published = (PublishAgentPackageCommand)c)
            .ReturnsAsync(Published("macos", "0.5.0"));

        await CreateHandler().Handle(new ImportAgentPackagesFromSourceCommand(ApiBaseUrl), CancellationToken.None);

        Assert.Equal(2000, published!.ReleaseNotes!.Length);
    }

    [Fact]
    public async Task Handle_NothingPublishedUpstream_ReturnsNoResults()
    {
        SourceHas();

        Assert.Empty(await CreateHandler().Handle(new ImportAgentPackagesFromSourceCommand(ApiBaseUrl), CancellationToken.None));
    }
}
