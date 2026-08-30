using System.Security.Cryptography;
using Moq;
using Kintsugi.Application.AgentPackages;
using Kintsugi.Application.AgentPackages.Commands.PublishAgentPackage;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Application.AgentPackages;

public class PublishAgentPackageCommandHandlerTests
{
    private static readonly byte[] DefaultContentBytes = { 1, 2, 3 };

    private readonly Mock<IAgentPackageRepository> _repository = new();
    private readonly Mock<IAgentPackageStorage> _storage = new();
    private readonly Mock<IArtifactSigningService> _signingService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private PublishAgentPackageCommandHandler CreateHandler() =>
        new(_repository.Object, _storage.Object, _signingService.Object, _unitOfWork.Object);

    private static PublishAgentPackageCommand Command(string platform = "macOS", string version = "0.2.0", byte[]? contentBytes = null) =>
        new(platform, version, "Fixes self-update.", "kintsugi-agent-macos-0.2.0.tar.gz", new MemoryStream(contentBytes ?? DefaultContentBytes));

    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task Handle_WhenNotAlreadyPublished_SavesTheFileSignsItAndPersistsIt()
    {
        _repository.Setup(r => r.GetByPlatformAndVersionAsync("macos", "0.2.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentPackage?)null);
        _storage.Setup(s => s.SaveAsync("macos", "kintsugi-agent-macos-0.2.0.tar.gz", It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((1024L, new string('a', 64)));
        _signingService.Setup(s => s.Sign(new string('a', 64))).Returns("signature");

        var result = await CreateHandler().Handle(Command(), CancellationToken.None);

        Assert.Equal("macos", result.Platform);
        Assert.Equal("0.2.0", result.Version);
        Assert.Equal(1024, result.FileSizeBytes);
        Assert.Equal(new string('a', 64), result.Sha256);
        Assert.Equal("signature", result.Sha256Signature);
        _repository.Verify(r => r.AddAsync(It.IsAny<AgentPackage>(), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_NormalizesThePlatformToLowercase_BeforeCheckingAndSaving()
    {
        _repository.Setup(r => r.GetByPlatformAndVersionAsync("macos", "0.2.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentPackage?)null);
        _storage.Setup(s => s.SaveAsync("macos", It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((1024L, new string('a', 64)));
        _signingService.Setup(s => s.Sign(It.IsAny<string>())).Returns("signature");

        var result = await CreateHandler().Handle(Command(platform: "macOS"), CancellationToken.None);

        Assert.Equal("macos", result.Platform);
    }

    [Fact]
    public async Task Handle_WhenThatPlatformAndVersionAlreadyExistsWithTheSameContent_IsIdempotentAndReturnsTheExistingRecord()
    {
        // Simulates a CI job (or a re-run of the same job) calling this again for a build it's
        // already published — the whole point of making this idempotent.
        var existing = AgentPackage.Create("macos", "0.2.0", "file.tar.gz", 1024, Sha256Of(DefaultContentBytes), "sig", "original notes");
        _repository.Setup(r => r.GetByPlatformAndVersionAsync("macos", "0.2.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var result = await CreateHandler().Handle(Command(), CancellationToken.None);

        Assert.Equal(AgentPackageDto.FromEntity(existing), result);
        _storage.Verify(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
        _signingService.Verify(s => s.Sign(It.IsAny<string>()), Times.Never);
        _repository.Verify(r => r.AddAsync(It.IsAny<AgentPackage>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenThatPlatformAndVersionAlreadyExistsWithDifferentContent_Throws()
    {
        // A version number that's already published but shows up with different bytes almost
        // always means someone forgot to bump the version — that must still fail loudly rather
        // than silently keeping the old (now stale) published content.
        var existing = AgentPackage.Create("macos", "0.2.0", "file.tar.gz", 1024, new string('a', 64), "sig", null);
        _repository.Setup(r => r.GetByPlatformAndVersionAsync("macos", "0.2.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        await Assert.ThrowsAsync<DomainException>(() => CreateHandler().Handle(Command(), CancellationToken.None));

        _storage.Verify(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
