using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ManagedApplications.Queries.GetManagedApplications;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.ManagedApplications.Commands.RemoveManagedOwner;

public class RemoveManagedOwnerCommandHandler : IRequestHandler<RemoveManagedOwnerCommand, ManagedApplicationsDto>
{
    private readonly IManagedApplicationRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISender _sender;

    public RemoveManagedOwnerCommandHandler(IManagedApplicationRepository repository, IUnitOfWork unitOfWork, ISender sender)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _sender = sender;
    }

    public async Task<ManagedApplicationsDto> Handle(RemoveManagedOwnerCommand request, CancellationToken cancellationToken)
    {
        var owner = await _repository.FindOwnerAsync(request.Name, cancellationToken);
        if (owner is not null)
        {
            if (await _repository.OwnerIsInUseAsync(owner.Name, cancellationToken))
            {
                throw new DomainException(
                    $"{owner.Name} still patches some applications. Move them to another tool, or give them back to Kintsugi, first.");
            }

            _repository.RemoveOwner(owner);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await _sender.Send(new GetManagedApplicationsQuery(), cancellationToken);
    }
}
