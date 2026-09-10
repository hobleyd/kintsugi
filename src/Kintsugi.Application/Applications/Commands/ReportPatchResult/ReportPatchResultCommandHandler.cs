using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Applications.Commands.ReportPatchResult;

public class ReportPatchResultCommandHandler : IRequestHandler<ReportPatchResultCommand, Unit>
{
    private readonly IHostRepository _hostRepository;
    private readonly IInstalledApplicationRepository _installedApplicationRepository;
    private readonly IPatchFailureRepository _patchFailureRepository;
    private readonly IUnitOfWork _unitOfWork;

    public ReportPatchResultCommandHandler(
        IHostRepository hostRepository,
        IInstalledApplicationRepository installedApplicationRepository,
        IPatchFailureRepository patchFailureRepository,
        IUnitOfWork unitOfWork)
    {
        _hostRepository = hostRepository;
        _installedApplicationRepository = installedApplicationRepository;
        _patchFailureRepository = patchFailureRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Unit> Handle(ReportPatchResultCommand request, CancellationToken cancellationToken)
    {
        var host = await _hostRepository.GetBySerialNumberAsync(request.SerialNumber, cancellationToken)
            ?? throw new NotFoundException($"No host is registered with serial number '{request.SerialNumber}'.");

        // Closed before the application lookup below, and regardless of how that turns out: a
        // success is a success whether or not this server still tracks the row it updates, and a
        // failure left open after the thing it complains about started working is a queue entry
        // nobody can clear. Without this the Failed Updates screen only ever grows — every broken
        // script that gets fixed leaves its failure sitting there looking live.
        var outstanding = await _patchFailureRepository.GetOutstandingForApplicationAsync(host.Id, request.ApplicationName, cancellationToken);
        foreach (var failure in outstanding)
        {
            failure.Resolve(PatchFailureResolution.PatchSucceeded);
        }

        var application = await _installedApplicationRepository.GetByHostIdAndNameAsync(host.Id, request.ApplicationName, cancellationToken);
        if (application is null)
        {
            // Not an error — e.g. a report that raced this host's next full inventory report, or
            // an application the server no longer tracks for it. Nothing to update, so nothing is
            // written *unless* a failure was just closed above: a success is a success whether or
            // not this server still tracks the row it would have updated, and returning early
            // without saving would leave that failure open on the screen forever.
            if (outstanding.Count > 0)
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return Unit.Value;
        }

        application.UpdateVersion(request.NewVersion);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
