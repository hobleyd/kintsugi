using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Hosts.Commands.RequestHostRemoval;

public class RequestHostRemovalCommandHandler : IRequestHandler<RequestHostRemovalCommand, Unit>
{
    private readonly IHostRepository _hostRepository;
    private readonly IUnitOfWork _unitOfWork;

    public RequestHostRemovalCommandHandler(IHostRepository hostRepository, IUnitOfWork unitOfWork)
    {
        _hostRepository = hostRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Unit> Handle(RequestHostRemovalCommand request, CancellationToken cancellationToken)
    {
        var host = await _hostRepository.GetByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"No host is registered with id '{request.Id}'.");

        host.RequestRemoval();
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
