using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Hosts.Commands.ReportOperatingSystemPatched;

public class ReportOperatingSystemPatchedCommandHandler : IRequestHandler<ReportOperatingSystemPatchedCommand, Unit>
{
    private readonly IHostRepository _hostRepository;
    private readonly IUnitOfWork _unitOfWork;

    public ReportOperatingSystemPatchedCommandHandler(IHostRepository hostRepository, IUnitOfWork unitOfWork)
    {
        _hostRepository = hostRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Unit> Handle(ReportOperatingSystemPatchedCommand request, CancellationToken cancellationToken)
    {
        var host = await _hostRepository.GetBySerialNumberAsync(request.SerialNumber, cancellationToken)
            ?? throw new NotFoundException($"No host is registered with serial number '{request.SerialNumber}'.");

        host.RecordOperatingSystemPatched();
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
