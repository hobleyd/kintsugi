using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.Hosts.Commands.ConfirmHostRemoval;

public class ConfirmHostRemovalCommandHandler : IRequestHandler<ConfirmHostRemovalCommand, Unit>
{
    private readonly IHostRepository _hostRepository;
    private readonly IUnitOfWork _unitOfWork;

    public ConfirmHostRemovalCommandHandler(IHostRepository hostRepository, IUnitOfWork unitOfWork)
    {
        _hostRepository = hostRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Unit> Handle(ConfirmHostRemovalCommand request, CancellationToken cancellationToken)
    {
        var host = await _hostRepository.GetBySerialNumberAsync(request.SerialNumber, cancellationToken)
            ?? throw new NotFoundException($"No host is registered with serial number '{request.SerialNumber}'.");

        if (!host.RemovalRequested)
        {
            throw new DomainException($"Host '{request.SerialNumber}' was not marked for removal — refusing to delete it.");
        }

        await _hostRepository.DeleteAsync(host, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
