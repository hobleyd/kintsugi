using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ForcedPatchRuns.Commands.ClaimForcedPatchRuns;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Tests.Application.ForcedPatchRuns;

public class ClaimForcedPatchRunsCommandHandlerTests
{
    private readonly Mock<IHostRepository> _hostRepository = new();
    private readonly Mock<IForcedPatchRunRepository> _forcedPatchRunRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Host _host = new("studio", "SERIAL-MAC", "macOS 15.0");

    public ClaimForcedPatchRunsCommandHandlerTests()
    {
        _hostRepository
            .Setup(r => r.GetBySerialNumberAsync("SERIAL-MAC", It.IsAny<CancellationToken>()))
            .ReturnsAsync(_host);
    }

    private ClaimForcedPatchRunsCommandHandler CreateHandler() =>
        new(_hostRepository.Object, _forcedPatchRunRepository.Object, _unitOfWork.Object);

    /// <summary>
    /// Reading is what delivers the instruction, so the read consumes it. A row left collectable
    /// would be served again sixty seconds later, and every serving starts a five-minute patching
    /// warning on the host.
    /// </summary>
    [Fact]
    public async Task Handle_MarksEveryRunItHandsOverAsCollected()
    {
        var run = ForcedPatchRun.Request(_host.Id, "Firefox", "macOS", DateTimeOffset.UtcNow, ForcedPatchRun.DefaultLifetime);
        _forcedPatchRunRepository
            .Setup(r => r.GetCollectableForHostAsync(_host.Id, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { run });

        var claimed = await CreateHandler().Handle(new ClaimForcedPatchRunsCommand("SERIAL-MAC"), CancellationToken.None);

        Assert.Single(claimed);
        Assert.Equal("Firefox", claimed[0].ApplicationName);
        Assert.NotNull(run.CollectedUtc);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WritesNothingWhenThereIsNothingToHandOver()
    {
        _forcedPatchRunRepository
            .Setup(r => r.GetCollectableForHostAsync(_host.Id, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForcedPatchRun>());

        Assert.Empty(await CreateHandler().Handle(new ClaimForcedPatchRunsCommand("SERIAL-MAC"), CancellationToken.None));

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Polled once a minute for the life of the agent process, so an unknown host is an empty list
    /// rather than a 404 sixty times an hour.
    /// </summary>
    [Fact]
    public async Task Handle_AnswersAnUnknownHostWithNothingRatherThanFailing()
    {
        _hostRepository
            .Setup(r => r.GetBySerialNumberAsync("MISSING", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Host?)null);

        Assert.Empty(await CreateHandler().Handle(new ClaimForcedPatchRunsCommand("MISSING"), CancellationToken.None));
    }
}
