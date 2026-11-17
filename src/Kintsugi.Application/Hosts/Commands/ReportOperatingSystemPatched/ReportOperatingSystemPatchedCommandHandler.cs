using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.HostActivity;
using Kintsugi.Application.HostActivity.Queries.GetHostActivity;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Hosts.Commands.ReportOperatingSystemPatched;

public class ReportOperatingSystemPatchedCommandHandler : IRequestHandler<ReportOperatingSystemPatchedCommand, Unit>
{
    /// <summary>
    /// The application name the macOS agent files an OS-update failure under on the Failed Updates
    /// screen — <c>OS_FAILURE_APPLICATION_NAME</c> in <c>clients/macos-agent/src/os_update.rs</c>,
    /// which reuses the application-failure route rather than having one of its own. The two have
    /// to agree, or a success reported here closes nothing.
    /// </summary>
    public const string OperatingSystemFailureApplicationName = "macOS";

    /// <summary>
    /// The same, for the Windows agent — <c>OS_FAILURE_APPLICATION_NAME</c> in
    /// <c>clients/windows-agent/src/os_update.rs</c>. A host only ever has rows under its own
    /// operating system's name, so closing both names for every host is harmless and saves this
    /// handler guessing the platform from a free-text operating-system string.
    /// </summary>
    public const string WindowsFailureApplicationName = "Windows";

    /// <summary>The same, for the Linux agent — <c>OS_FAILURE_APPLICATION_NAME</c> in
    /// <c>clients/linux-agent/src/os_update.rs</c>.</summary>
    public const string LinuxFailureApplicationName = "Linux";

    private readonly IHostRepository _hostRepository;
    private readonly IPatchFailureRepository _patchFailureRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IHostEventRecorder _events;

    public ReportOperatingSystemPatchedCommandHandler(
        IHostRepository hostRepository,
        IPatchFailureRepository patchFailureRepository,
        IUnitOfWork unitOfWork,
        IHostEventRecorder events)
    {
        _hostRepository = hostRepository;
        _patchFailureRepository = patchFailureRepository;
        _unitOfWork = unitOfWork;
        _events = events;
    }

    public async Task<Unit> Handle(ReportOperatingSystemPatchedCommand request, CancellationToken cancellationToken)
    {
        var host = await _hostRepository.GetBySerialNumberAsync(request.SerialNumber, cancellationToken)
            ?? throw new NotFoundException($"No host is registered with serial number '{request.SerialNumber}'.");

        // The same rule ReportPatchResultCommandHandler applies to an application: a failure left
        // open after the thing it complains about succeeded is a queue entry nobody can clear.
        // Nothing else ever closes a macOS row — no script is re-signed for it, and the next
        // check-in only re-derives the pending flag — so until this existed, a Mac whose install
        // had failed once and then succeeded kept its failure on the Failed Updates screen for
        // good. On this fleet's own Mac a download failure from 17 September outlived the
        // successful install on the 22nd.
        foreach (var name in new[] { OperatingSystemFailureApplicationName, WindowsFailureApplicationName, LinuxFailureApplicationName })
        {
            var outstanding = await _patchFailureRepository.GetOutstandingForApplicationAsync(
                host.Id, name, cancellationToken);
            foreach (var failure in outstanding)
            {
                failure.Resolve(PatchFailureResolution.PatchSucceeded);
            }
        }

        host.RecordOperatingSystemPatched();

        // Read by the agent straight after the install, so a kernel that was just updated shows as
        // needing a restart now and not at the next hourly check-in. Null (an agent that cannot say)
        // leaves the stored state alone.
        host.RecordRestartState(request.RestartRequired, request.RestartBecause, DateTimeOffset.UtcNow);
        await _events.OperatingSystemPatchedAsync(
            host.Id, GetHostActivityQueryHandler.OperatingSystemName(host.OperatingSystem), DateTimeOffset.UtcNow, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
