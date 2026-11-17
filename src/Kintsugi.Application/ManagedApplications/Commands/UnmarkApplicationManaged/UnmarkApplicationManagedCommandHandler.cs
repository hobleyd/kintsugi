using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ManagedApplications.Queries.GetManagedApplications;

namespace Kintsugi.Application.ManagedApplications.Commands.UnmarkApplicationManaged;

public class UnmarkApplicationManagedCommandHandler : IRequestHandler<UnmarkApplicationManagedCommand, ManagedApplicationsDto>
{
    private readonly IManagedApplicationRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISender _sender;

    public UnmarkApplicationManagedCommandHandler(IManagedApplicationRepository repository, IUnitOfWork unitOfWork, ISender sender)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _sender = sender;
    }

    public async Task<ManagedApplicationsDto> Handle(UnmarkApplicationManagedCommand request, CancellationToken cancellationToken)
    {
        var existing = await _repository.FindAsync(request.ApplicationName, cancellationToken);
        if (existing is not null)
        {
            _repository.Remove(existing);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await _sender.Send(new GetManagedApplicationsQuery(), cancellationToken);
    }
}
