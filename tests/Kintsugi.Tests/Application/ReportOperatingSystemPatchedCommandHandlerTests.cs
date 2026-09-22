using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Hosts.Commands.ReportOperatingSystemPatched;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application;

public class ReportOperatingSystemPatchedCommandHandlerTests
{
    private readonly Mock<IHostRepository> _hostRepository = new();
    private readonly Mock<IPatchFailureRepository> _patchFailureRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public ReportOperatingSystemPatchedCommandHandlerTests()
    {
        // Moq answers an unstubbed IReadOnlyList-returning method with null rather than an empty
        // list, so the tests that are not about failures need this stubbed.
        _patchFailureRepository
            .Setup(r => r.GetOutstandingForApplicationAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private ReportOperatingSystemPatchedCommandHandler CreateHandler() =>
        new(_hostRepository.Object, _patchFailureRepository.Object, _unitOfWork.Object);

    [Fact]
    public async Task Handle_WhenNoHostWithThatSerialNumberIsRegistered_ThrowsNotFound()
    {
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("MISSING", It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => CreateHandler().Handle(
            new ReportOperatingSystemPatchedCommand("MISSING"), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_ClearsThePendingUpdateFlagAndTargetVersion_AndSaves()
    {
        var host = new Host("host-1", "SERIAL-1", operatingSystemUpdateAvailable: true, operatingSystemLatestVersion: "15.1");
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync(host);

        await CreateHandler().Handle(new ReportOperatingSystemPatchedCommand("SERIAL-1"), CancellationToken.None);

        Assert.False(host.OperatingSystemUpdateAvailable);
        Assert.Null(host.OperatingSystemLatestVersion);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The macOS agent files an OS-update failure under the application name "macOS" (its
    /// <c>OS_FAILURE_APPLICATION_NAME</c>), and nothing but a later success closes that row. A
    /// download failure that stayed on the Failed Updates screen through a successful install is
    /// what this exists to stop.
    /// </summary>
    [Fact]
    public async Task Handle_ClosesTheOutstandingMacOsFailureRowsForThatHost()
    {
        var host = new Host("host-1", "SERIAL-1", operatingSystemUpdateAvailable: true, operatingSystemLatestVersion: "27");
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync(host);
        var failure = PatchFailure.Open(
            host.Id,
            ReportOperatingSystemPatchedCommandHandler.OperatingSystemFailureApplicationName,
            platform: null,
            installedVersion: "macOS 26.6.2",
            attemptedVersion: "27",
            details: "softwareupdate could not download 1 of the 3 pending update(s)",
            failedUtc: DateTimeOffset.UtcNow);
        _patchFailureRepository
            .Setup(r => r.GetOutstandingForApplicationAsync(host.Id, "macOS", It.IsAny<CancellationToken>()))
            .ReturnsAsync([failure]);

        await CreateHandler().Handle(new ReportOperatingSystemPatchedCommand("SERIAL-1"), CancellationToken.None);

        Assert.Equal(PatchFailureResolution.PatchSucceeded, failure.Resolution);
        Assert.NotNull(failure.ResolvedUtc);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_LooksUpFailuresUnderTheNameTheMacOsAgentFilesThemUnder()
    {
        var host = new Host("host-1", "SERIAL-1", operatingSystemUpdateAvailable: true, operatingSystemLatestVersion: "27");
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync(host);

        await CreateHandler().Handle(new ReportOperatingSystemPatchedCommand("SERIAL-1"), CancellationToken.None);

        // "macOS" is OS_FAILURE_APPLICATION_NAME in clients/macos-agent/src/os_update.rs.
        _patchFailureRepository.Verify(r => r.GetOutstandingForApplicationAsync(host.Id, "macOS", It.IsAny<CancellationToken>()), Times.Once);
    }
}
