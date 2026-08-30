using MediatR;

namespace Kintsugi.Application.Deployments.Commands.ScheduleDeployment;

public record ScheduleDeploymentCommand(Guid HostId, Guid PatchId, DateTimeOffset ScheduledForUtc) : IRequest<PatchDeploymentDto>;
