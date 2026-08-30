using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

public class PatchDeployment : BaseEntity
{
    public Guid HostId { get; private set; }
    public Guid PatchId { get; private set; }
    public DeploymentStatus Status { get; private set; } = DeploymentStatus.Scheduled;
    public DateTimeOffset ScheduledForUtc { get; private set; }
    public DateTimeOffset? CompletedUtc { get; private set; }
    public string? FailureReason { get; private set; }

    private PatchDeployment()
    {
    }

    public PatchDeployment(Guid hostId, Guid patchId, DateTimeOffset scheduledForUtc)
    {
        if (hostId == Guid.Empty)
        {
            throw new DomainException("HostId is required.");
        }

        if (patchId == Guid.Empty)
        {
            throw new DomainException("PatchId is required.");
        }

        HostId = hostId;
        PatchId = patchId;
        ScheduledForUtc = scheduledForUtc;
    }

    public void Start()
    {
        if (Status != DeploymentStatus.Scheduled)
        {
            throw new DomainException($"Cannot start a deployment in status {Status}.");
        }

        Status = DeploymentStatus.InProgress;
        MarkUpdated();
    }

    public void Complete()
    {
        if (Status != DeploymentStatus.InProgress)
        {
            throw new DomainException($"Cannot complete a deployment in status {Status}.");
        }

        Status = DeploymentStatus.Succeeded;
        CompletedUtc = DateTimeOffset.UtcNow;
        MarkUpdated();
    }

    public void Fail(string reason)
    {
        if (Status != DeploymentStatus.InProgress)
        {
            throw new DomainException($"Cannot fail a deployment in status {Status}.");
        }

        Status = DeploymentStatus.Failed;
        FailureReason = reason;
        CompletedUtc = DateTimeOffset.UtcNow;
        MarkUpdated();
    }
}
