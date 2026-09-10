using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.PatchFailures.Commands.ReportPatchFailure;

public class ReportPatchFailureCommandHandler : IRequestHandler<ReportPatchFailureCommand, Unit>
{
    private readonly IHostRepository _hostRepository;
    private readonly IPatchFailureRepository _patchFailureRepository;
    private readonly IUpgradePathRepository _upgradePathRepository;
    private readonly IUnitOfWork _unitOfWork;

    public ReportPatchFailureCommandHandler(
        IHostRepository hostRepository,
        IPatchFailureRepository patchFailureRepository,
        IUpgradePathRepository upgradePathRepository,
        IUnitOfWork unitOfWork)
    {
        _hostRepository = hostRepository;
        _patchFailureRepository = patchFailureRepository;
        _upgradePathRepository = upgradePathRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Unit> Handle(ReportPatchFailureCommand request, CancellationToken cancellationToken)
    {
        var host = await _hostRepository.GetBySerialNumberAsync(request.SerialNumber, cancellationToken)
            ?? throw new NotFoundException($"No host is registered with serial number '{request.SerialNumber}'.");

        // The platform bucket comes from re-running the same (host, application) resolution that
        // served this agent its work list, rather than from anything the agent said — see the
        // remarks on PatchFailure for why. Null when nothing resolves any more (the row was deleted
        // between the agent fetching it and the failure being reported), which the screen shows as
        // a failure it cannot offer a fix for.
        var path = await _upgradePathRepository.ResolveForHostAsync(request.SerialNumber, request.ApplicationName, cancellationToken);

        var existing = await _patchFailureRepository.GetByHostAndApplicationAsync(host.Id, request.ApplicationName, cancellationToken);

        if (existing is null)
        {
            await _patchFailureRepository.AddAsync(
                PatchFailure.Open(
                    host.Id, request.ApplicationName, path?.Platform,
                    request.InstalledVersion, request.AttemptedVersion, request.Details, request.FailedUtc),
                cancellationToken);
        }
        else if (existing.Resolution == PatchFailureResolution.Outstanding)
        {
            existing.RecordAnotherFailure(path?.Platform, request.InstalledVersion, request.AttemptedVersion, request.Details, request.FailedUtc);
        }
        else
        {
            existing.Reopen(path?.Platform, request.InstalledVersion, request.AttemptedVersion, request.Details, request.FailedUtc);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
