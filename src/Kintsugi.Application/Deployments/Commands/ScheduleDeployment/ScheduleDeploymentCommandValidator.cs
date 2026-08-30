using FluentValidation;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Deployments.Commands.ScheduleDeployment;

public class ScheduleDeploymentCommandValidator : AbstractValidator<ScheduleDeploymentCommand>
{
    public ScheduleDeploymentCommandValidator(IHostRepository hostRepository, IPatchRepository patchRepository)
    {
        RuleFor(x => x.HostId)
            .MustAsync((id, ct) => ExistsAsync(hostRepository, id, ct))
            .WithMessage("Host does not exist.");

        RuleFor(x => x.PatchId)
            .MustAsync((id, ct) => ExistsAsync(patchRepository, id, ct))
            .WithMessage("Patch does not exist.");
    }

    private static async Task<bool> ExistsAsync(IHostRepository repository, Guid id, CancellationToken cancellationToken) =>
        await repository.GetByIdAsync(id, cancellationToken) is not null;

    private static async Task<bool> ExistsAsync(IPatchRepository repository, Guid id, CancellationToken cancellationToken) =>
        await repository.GetByIdAsync(id, cancellationToken) is not null;
}
