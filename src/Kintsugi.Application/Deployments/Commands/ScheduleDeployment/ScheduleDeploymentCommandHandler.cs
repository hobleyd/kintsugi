using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Deployments.Commands.ScheduleDeployment;

public class ScheduleDeploymentCommandHandler : IRequestHandler<ScheduleDeploymentCommand, PatchDeploymentDto>
{
    private readonly IPatchDeploymentRepository _deploymentRepository;
    private readonly IUnitOfWork _unitOfWork;

    public ScheduleDeploymentCommandHandler(IPatchDeploymentRepository deploymentRepository, IUnitOfWork unitOfWork)
    {
        _deploymentRepository = deploymentRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<PatchDeploymentDto> Handle(ScheduleDeploymentCommand request, CancellationToken cancellationToken)
    {
        var deployment = new PatchDeployment(request.HostId, request.PatchId, request.ScheduledForUtc);

        await _deploymentRepository.AddAsync(deployment, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return PatchDeploymentDto.FromEntity(deployment);
    }
}
