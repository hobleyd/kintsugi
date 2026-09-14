using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ForcedPatchRuns.Commands.RequestForcedPatchRuns;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Tests.Application.ForcedPatchRuns;

public class RequestForcedPatchRunsCommandHandlerTests
{
    private readonly Mock<IHostRepository> _hostRepository = new();
    private readonly Mock<IForcedPatchRunRepository> _forcedPatchRunRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private readonly Host _mac = new("studio", "SERIAL-MAC", "macOS 15.0");
    private readonly Host _pc = new("reception", "SERIAL-PC", "Windows 11");

    public RequestForcedPatchRunsCommandHandlerTests()
    {
        _hostRepository
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { _mac, _pc });
    }

    private RequestForcedPatchRunsCommandHandler CreateHandler() =>
        new(_hostRepository.Object, _forcedPatchRunRepository.Object, _unitOfWork.Object);

    private static RequestForcedPatchRunsCommand Command(params string[] hostNames) =>
        new("Firefox", "macOS", hostNames);

    [Fact]
    public async Task Handle_RaisesOneRunPerNamedHost()
    {
        var added = new List<ForcedPatchRun>();
        _forcedPatchRunRepository
            .Setup(r => r.AddAsync(It.IsAny<ForcedPatchRun>(), It.IsAny<CancellationToken>()))
            .Callback<ForcedPatchRun, CancellationToken>((run, _) => added.Add(run));

        var result = await CreateHandler().Handle(Command("studio", "reception"), CancellationToken.None);

        Assert.Equal(2, result.Requested);
        Assert.Empty(result.NotRequested);
        Assert.Equal(new[] { _mac.Id, _pc.Id }, added.Select(run => run.HostId));
        Assert.All(added, run => Assert.Equal("Firefox", run.ApplicationName));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The interesting half of the answer is the hosts it could not reach. A filter resolved in the
    /// browser can name a host that has since been removed, and an operator acting on an emergency
    /// has to see that the machine they were looking at is not one of the ones that was told.
    /// </summary>
    [Fact]
    public async Task Handle_ReportsAHostItCouldNotFindRatherThanFailingTheWholeRequest()
    {
        var result = await CreateHandler().Handle(Command("studio", "decommissioned"), CancellationToken.None);

        Assert.Equal(1, result.Requested);
        Assert.Equal(new[] { "decommissioned" }, result.NotRequested);
        _forcedPatchRunRepository.Verify(r => r.AddAsync(It.IsAny<ForcedPatchRun>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A filter can name the same host through two rows, and two instructions for one machine are
    /// one instruction.
    /// </summary>
    [Fact]
    public async Task Handle_RaisesOneRunForAHostNamedTwice()
    {
        var result = await CreateHandler().Handle(Command("studio", "STUDIO"), CancellationToken.None);

        Assert.Equal(1, result.Requested);
        _forcedPatchRunRepository.Verify(r => r.AddAsync(It.IsAny<ForcedPatchRun>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Two presses thirty seconds apart mean "patch this, now" once. A second row would have the
    /// agent collect one, patch, and then find the other still waiting on its next poll.
    /// </summary>
    [Fact]
    public async Task Handle_RenewsAnUncollectedRunRatherThanOpeningASecond()
    {
        var existing = ForcedPatchRun.Request(
            _mac.Id, "Firefox", "macOS", DateTimeOffset.UtcNow.AddHours(-20), ForcedPatchRun.DefaultLifetime);
        _forcedPatchRunRepository
            .Setup(r => r.GetOutstandingAsync(_mac.Id, "Firefox", "macOS", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var result = await CreateHandler().Handle(Command("studio"), CancellationToken.None);

        Assert.Equal(1, result.Requested);
        _forcedPatchRunRepository.Verify(r => r.AddAsync(It.IsAny<ForcedPatchRun>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(existing.ExpiresUtc > DateTimeOffset.UtcNow.AddHours(23), "the renewed row gets a fresh lifetime");
    }
}
