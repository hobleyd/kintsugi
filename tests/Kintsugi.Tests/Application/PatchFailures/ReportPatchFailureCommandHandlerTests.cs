using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.PatchFailures.Commands.ReportPatchFailure;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.PatchFailures;

public class ReportPatchFailureCommandHandlerTests
{
    private readonly Mock<IHostRepository> _hostRepository = new();
    private readonly Mock<IPatchFailureRepository> _patchFailureRepository = new();
    private readonly Mock<IUpgradePathRepository> _upgradePathRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Host _host = new("host-1", "SERIAL-1", "macOS 15.0");

    private static readonly DateTimeOffset Monday = new(2026, 9, 7, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Tuesday = new(2026, 9, 8, 2, 0, 0, TimeSpan.Zero);

    public ReportPatchFailureCommandHandlerTests()
    {
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync(_host);
    }

    private ReportPatchFailureCommandHandler CreateHandler() =>
        new(_hostRepository.Object, _patchFailureRepository.Object, _upgradePathRepository.Object, _unitOfWork.Object);

    private static ReportPatchFailureCommand Command(DateTimeOffset failedUtc, string details = "exited with 1: Permission denied") =>
        new("SERIAL-1", "Ollama", "0.32.14", "0.33.3", failedUtc, details);

    [Fact]
    public async Task Handle_WhenNoHostWithThatSerialNumberIsRegistered_ThrowsNotFound()
    {
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("MISSING", It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => CreateHandler().Handle(
            new ReportPatchFailureCommand("MISSING", "Ollama", null, null, Monday, "boom"), CancellationToken.None));
    }

    /// <summary>
    /// The platform is the bucket the failing script is *stored* under, and for a
    /// package-manager-managed installation that is the manager's name, not the host's OS. The
    /// agent never sends it for exactly this reason — see PatchFailure's remarks.
    /// </summary>
    [Fact]
    public async Task Handle_TakesThePlatformFromTheServersOwnResolution_NotFromTheHostsOperatingSystem()
    {
        _upgradePathRepository
            .Setup(r => r.ResolveForHostAsync("SERIAL-1", "Ollama", It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpgradePath.Create("Ollama", "Homebrew", UpgradePathStatus.Found, "0.33.3", UpgradeMethod.Script, null, null, null, null, null, "#!/bin/bash\n"));

        PatchFailure? added = null;
        _patchFailureRepository.Setup(r => r.AddAsync(It.IsAny<PatchFailure>(), It.IsAny<CancellationToken>()))
            .Callback<PatchFailure, CancellationToken>((f, _) => added = f);

        await CreateHandler().Handle(Command(Monday), CancellationToken.None);

        Assert.NotNull(added);
        Assert.Equal("Homebrew", added!.Platform);
    }

    [Fact]
    public async Task Handle_WhenNoUpgradePathResolves_StillRecordsTheFailureWithNoPlatform()
    {
        // The row was deleted between the agent fetching its work list and reporting the failure.
        // Losing the report would hide a real failure; the screen shows it as one it cannot fix.
        PatchFailure? added = null;
        _patchFailureRepository.Setup(r => r.AddAsync(It.IsAny<PatchFailure>(), It.IsAny<CancellationToken>()))
            .Callback<PatchFailure, CancellationToken>((f, _) => added = f);

        await CreateHandler().Handle(Command(Monday), CancellationToken.None);

        Assert.NotNull(added);
        Assert.Null(added!.Platform);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A broken script fails again every patch cycle. One row per attempt would bury the handful of
    /// distinct problems an administrator can act on.
    /// </summary>
    [Fact]
    public async Task Handle_FoldsARepeatIntoTheExistingRow_RatherThanOpeningASecondOne()
    {
        var existing = PatchFailure.Open(_host.Id, "Ollama", "macOS", "0.32.14", "0.33.3", "exited with 1: Permission denied", Monday);
        _patchFailureRepository
            .Setup(r => r.GetByHostAndApplicationAsync(_host.Id, "Ollama", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        await CreateHandler().Handle(Command(Tuesday, "exited with 2: No such file or directory"), CancellationToken.None);

        _patchFailureRepository.Verify(r => r.AddAsync(It.IsAny<PatchFailure>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(2, existing.FailureCount);
        Assert.Equal(Monday, existing.FirstFailedUtc);
        Assert.Equal(Tuesday, existing.LastFailedUtc);
        // The latest message wins: it describes the script as it is now.
        Assert.Equal("exited with 2: No such file or directory", existing.Details);
    }

    [Fact]
    public async Task Handle_ReopensASettledRow_RatherThanOpeningASecondOne()
    {
        var existing = PatchFailure.Open(_host.Id, "Ollama", "macOS", "0.32.14", "0.33.3", "exited with 1", Monday);
        existing.Resolve(PatchFailureResolution.Dismissed);
        _patchFailureRepository
            .Setup(r => r.GetByHostAndApplicationAsync(_host.Id, "Ollama", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        await CreateHandler().Handle(Command(Tuesday), CancellationToken.None);

        _patchFailureRepository.Verify(r => r.AddAsync(It.IsAny<PatchFailure>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(PatchFailureResolution.Outstanding, existing.Resolution);
        Assert.Null(existing.ResolvedUtc);
        Assert.Equal(Monday, existing.FirstFailedUtc);
    }

    /// <summary>
    /// Clock skew, or two reports arriving out of order, must not wind the date the screen sorts on
    /// backwards.
    /// </summary>
    [Fact]
    public async Task Handle_DoesNotMoveLastFailedBackwards_WhenAnOlderReportArrivesLate()
    {
        var existing = PatchFailure.Open(_host.Id, "Ollama", "macOS", null, null, "exited with 1", Tuesday);
        _patchFailureRepository
            .Setup(r => r.GetByHostAndApplicationAsync(_host.Id, "Ollama", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        await CreateHandler().Handle(Command(Monday), CancellationToken.None);

        Assert.Equal(Tuesday, existing.LastFailedUtc);
    }
}
