using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ManagedApplications.Queries.GetManagedApplications;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.ManagedApplications.Commands.AddManagedOwner;

public class AddManagedOwnerCommandHandler : IRequestHandler<AddManagedOwnerCommand, ManagedApplicationsDto>
{
    private readonly IManagedApplicationRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISender _sender;

    public AddManagedOwnerCommandHandler(IManagedApplicationRepository repository, IUnitOfWork unitOfWork, ISender sender)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _sender = sender;
    }

    public async Task<ManagedApplicationsDto> Handle(AddManagedOwnerCommand request, CancellationToken cancellationToken)
    {
        var owner = ManagedOwner.Create(request.Name);
        if (await _repository.FindOwnerAsync(owner.Name, cancellationToken) is null)
        {
            await _repository.AddOwnerAsync(owner, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await _sender.Send(new GetManagedApplicationsQuery(), cancellationToken);
    }
}
