using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Applications.Commands.ReportPatchResult;

public class ReportPatchResultCommandHandler : IRequestHandler<ReportPatchResultCommand, Unit>
{
    private readonly IHostRepository _hostRepository;
    private readonly IInstalledApplicationRepository _installedApplicationRepository;
    private readonly IUnitOfWork _unitOfWork;

    public ReportPatchResultCommandHandler(
        IHostRepository hostRepository,
        IInstalledApplicationRepository installedApplicationRepository,
        IUnitOfWork unitOfWork)
    {
        _hostRepository = hostRepository;
        _installedApplicationRepository = installedApplicationRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Unit> Handle(ReportPatchResultCommand request, CancellationToken cancellationToken)
    {
        var host = await _hostRepository.GetBySerialNumberAsync(request.SerialNumber, cancellationToken)
            ?? throw new NotFoundException($"No host is registered with serial number '{request.SerialNumber}'.");

        var application = await _installedApplicationRepository.GetByHostIdAndNameAsync(host.Id, request.ApplicationName, cancellationToken);
        if (application is null)
        {
            // Not an error — e.g. a report that raced this host's next full inventory report, or
            // an application the server no longer tracks for it. Nothing to update.
            return Unit.Value;
        }

        application.UpdateVersion(request.NewVersion);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
