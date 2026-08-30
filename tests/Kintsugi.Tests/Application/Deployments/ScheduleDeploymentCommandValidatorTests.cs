using FluentValidation.TestHelper;
using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Deployments.Commands.ScheduleDeployment;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.Deployments;

public class ScheduleDeploymentCommandValidatorTests
{
    private readonly Mock<IHostRepository> _hostRepository = new();
    private readonly Mock<IPatchRepository> _patchRepository = new();

    private ScheduleDeploymentCommandValidator CreateValidator() => new(_hostRepository.Object, _patchRepository.Object);

    [Fact]
    public async Task Command_ForAnExistingHostAndPatch_IsValid()
    {
        var hostId = Guid.NewGuid();
        var patchId = Guid.NewGuid();
        _hostRepository.Setup(r => r.GetByIdAsync(hostId, It.IsAny<CancellationToken>())).ReturnsAsync(new Host("host-1", "SERIAL-1"));
        _patchRepository.Setup(r => r.GetByIdAsync(patchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Patch("name", "vendor", "1.0", PatchSeverity.Low, DateTimeOffset.UtcNow));

        var result = await CreateValidator().TestValidateAsync(new ScheduleDeploymentCommand(hostId, patchId, DateTimeOffset.UtcNow));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Command_ForANonExistentHost_IsRejected()
    {
        var patchId = Guid.NewGuid();
        _hostRepository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);
        _patchRepository.Setup(r => r.GetByIdAsync(patchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Patch("name", "vendor", "1.0", PatchSeverity.Low, DateTimeOffset.UtcNow));

        var result = await CreateValidator().TestValidateAsync(new ScheduleDeploymentCommand(Guid.NewGuid(), patchId, DateTimeOffset.UtcNow));

        result.ShouldHaveValidationErrorFor(c => c.HostId);
    }

    [Fact]
    public async Task Command_ForANonExistentPatch_IsRejected()
    {
        var hostId = Guid.NewGuid();
        _hostRepository.Setup(r => r.GetByIdAsync(hostId, It.IsAny<CancellationToken>())).ReturnsAsync(new Host("host-1", "SERIAL-1"));
        _patchRepository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Patch?)null);

        var result = await CreateValidator().TestValidateAsync(new ScheduleDeploymentCommand(hostId, Guid.NewGuid(), DateTimeOffset.UtcNow));

        result.ShouldHaveValidationErrorFor(c => c.PatchId);
    }
}
