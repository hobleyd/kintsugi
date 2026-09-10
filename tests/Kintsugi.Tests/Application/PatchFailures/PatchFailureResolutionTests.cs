using Moq;
using Kintsugi.Application.Applications.Commands.ReportPatchResult;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.PatchFailures;

/// <summary>
/// The half of this feature that keeps the Failed Updates screen a queue rather than an archive: a
/// success report closes whatever failure it contradicts.
/// </summary>
public class PatchFailureResolutionTests
{
    private readonly Mock<IHostRepository> _hostRepository = new();
    private readonly Mock<IInstalledApplicationRepository> _installedApplicationRepository = new();
    private readonly Mock<IPatchFailureRepository> _patchFailureRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Host _host = new("host-1", "SERIAL-1", "macOS 15.0");

    public PatchFailureResolutionTests()
    {
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync(_host);
    }

    private ReportPatchResultCommandHandler CreateHandler() =>
        new(_hostRepository.Object, _installedApplicationRepository.Object, _patchFailureRepository.Object, _unitOfWork.Object);

    private PatchFailure GivenAnOutstandingFailure()
    {
        var failure = PatchFailure.Open(_host.Id, "Ollama", "macOS", "0.32.14", "0.33.3", "exited with 1", DateTimeOffset.UtcNow);
        _patchFailureRepository
            .Setup(r => r.GetOutstandingForApplicationAsync(_host.Id, "Ollama", It.IsAny<CancellationToken>()))
            .ReturnsAsync([failure]);
        return failure;
    }

    [Fact]
    public async Task ReportingASuccess_ClosesTheOutstandingFailureForThatApplication()
    {
        var failure = GivenAnOutstandingFailure();
        _installedApplicationRepository
            .Setup(r => r.GetByHostIdAndNameAsync(_host.Id, "Ollama", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InstalledApplication(_host.Id, "Ollama", "0.32.14"));

        await CreateHandler().Handle(new ReportPatchResultCommand("SERIAL-1", "Ollama", "0.33.3"), CancellationToken.None);

        Assert.Equal(PatchFailureResolution.PatchSucceeded, failure.Resolution);
        Assert.NotNull(failure.ResolvedUtc);
    }

    /// <summary>
    /// A success is a success whether or not this server still tracks the installed row it would
    /// have updated — that early return used to skip the save entirely.
    /// </summary>
    [Fact]
    public async Task ReportingASuccess_ClosesTheFailureEvenWhenTheInstalledApplicationIsNoLongerTracked()
    {
        var failure = GivenAnOutstandingFailure();
        _installedApplicationRepository
            .Setup(r => r.GetByHostIdAndNameAsync(_host.Id, "Ollama", It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstalledApplication?)null);

        await CreateHandler().Handle(new ReportPatchResultCommand("SERIAL-1", "Ollama", "0.33.3"), CancellationToken.None);

        Assert.Equal(PatchFailureResolution.PatchSucceeded, failure.Resolution);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Success reports keep arriving on every cycle after a fix lands. Re-stamping the resolution
    /// each time would keep moving the date the row was actually settled.
    /// </summary>
    [Fact]
    public void ResolvingAnAlreadyResolvedFailure_LeavesTheOriginalResolutionAlone()
    {
        var failure = PatchFailure.Open(_host.Id, "Ollama", "macOS", null, null, "exited with 1", DateTimeOffset.UtcNow);
        failure.Resolve(PatchFailureResolution.Dismissed);
        var settledAt = failure.ResolvedUtc;

        failure.Resolve(PatchFailureResolution.PatchSucceeded);

        Assert.Equal(PatchFailureResolution.Dismissed, failure.Resolution);
        Assert.Equal(settledAt, failure.ResolvedUtc);
    }
}
