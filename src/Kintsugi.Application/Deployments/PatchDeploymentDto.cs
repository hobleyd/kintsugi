using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Deployments;

public record PatchDeploymentDto(
    Guid Id,
    Guid HostId,
    Guid PatchId,
    DeploymentStatus Status,
    DateTimeOffset ScheduledForUtc,
    DateTimeOffset? CompletedUtc,
    string? FailureReason)
{
    public static PatchDeploymentDto FromEntity(PatchDeployment deployment) =>
        new(deployment.Id, deployment.HostId, deployment.PatchId, deployment.Status, deployment.ScheduledForUtc, deployment.CompletedUtc, deployment.FailureReason);
}
