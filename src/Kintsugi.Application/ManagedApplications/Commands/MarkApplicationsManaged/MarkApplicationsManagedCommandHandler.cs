using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ManagedApplications.Queries.GetManagedApplications;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.ManagedApplications.Commands.MarkApplicationsManaged;

public class MarkApplicationsManagedCommandHandler : IRequestHandler<MarkApplicationsManagedCommand, ManagedApplicationsDto>
{
    private readonly IManagedApplicationRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISender _sender;

    public MarkApplicationsManagedCommandHandler(IManagedApplicationRepository repository, IUnitOfWork unitOfWork, ISender sender)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _sender = sender;
    }

    public async Task<ManagedApplicationsDto> Handle(MarkApplicationsManagedCommand request, CancellationToken cancellationToken)
    {
        // The owner as the list spells it, so "crowdstrike" typed into a box joins "CrowdStrike" rather
        // than becoming a second entry that looks the same.
        var owner = await ResolveOwnerAsync(request.Owner, cancellationToken);

        foreach (var name in request.Names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var existing = await _repository.FindAsync(name, cancellationToken);
            if (existing is null)
            {
                await _repository.AddAsync(ManagedApplication.Create(name, owner), cancellationToken);
            }
            else
            {
                existing.ReassignTo(owner);
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return await _sender.Send(new GetManagedApplicationsQuery(), cancellationToken);
    }

    private async Task<string> ResolveOwnerAsync(string requested, CancellationToken cancellationToken)
    {
        var onTheList = await _repository.FindOwnerAsync(requested, cancellationToken);
        if (onTheList is not null)
        {
            return onTheList.Name;
        }

        var created = ManagedOwner.Create(requested);
        await _repository.AddOwnerAsync(created, cancellationToken);
        return created.Name;
    }
}
