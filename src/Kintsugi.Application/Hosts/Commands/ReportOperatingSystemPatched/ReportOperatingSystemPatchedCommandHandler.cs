using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
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

    private readonly IHostRepository _hostRepository;
    private readonly IPatchFailureRepository _patchFailureRepository;
    private readonly IUnitOfWork _unitOfWork;

    public ReportOperatingSystemPatchedCommandHandler(
        IHostRepository hostRepository,
        IPatchFailureRepository patchFailureRepository,
        IUnitOfWork unitOfWork)
    {
        _hostRepository = hostRepository;
        _patchFailureRepository = patchFailureRepository;
        _unitOfWork = unitOfWork;
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
        var outstanding = await _patchFailureRepository.GetOutstandingForApplicationAsync(
            host.Id, OperatingSystemFailureApplicationName, cancellationToken);
        foreach (var failure in outstanding)
        {
            failure.Resolve(PatchFailureResolution.PatchSucceeded);
        }

        host.RecordOperatingSystemPatched();
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
