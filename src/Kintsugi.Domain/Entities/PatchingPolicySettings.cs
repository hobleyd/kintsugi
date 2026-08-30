using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// Singleton fleet-wide policy governing how often patching runs, and — when installing a patch
/// needs an application restart or a host reboot — how long that can be deferred and how many
/// times. The kintsugi-agent is expected to read this (via a future step) and enforce it locally
/// rather than the server pushing patches on a schedule itself.
/// </summary>
public class PatchingPolicySettings : BaseEntity
{
    /// <summary>How often the agent should apply patches — e.g. every 7 <see cref="IntervalUnit"/>.</summary>
    public int IntervalValue { get; private set; }
    public PatchingTimeUnit IntervalUnit { get; private set; }

    /// <summary>How long a single deferral lasts, when a restart/reboot is required and the user
    /// chooses to postpone it — e.g. 24 <see cref="DelayUnit"/>.</summary>
    public int DelayValue { get; private set; }
    public PatchingTimeUnit DelayUnit { get; private set; }

    /// <summary>How many times a restart/reboot can be deferred before the agent must force it
    /// through regardless. Zero means deferral isn't permitted at all.</summary>
    public int MaxDelayCount { get; private set; }

    private PatchingPolicySettings()
    {
    }

    public static PatchingPolicySettings Create(int intervalValue, PatchingTimeUnit intervalUnit, int delayValue, PatchingTimeUnit delayUnit, int maxDelayCount)
    {
        var settings = new PatchingPolicySettings();
        settings.Apply(intervalValue, intervalUnit, delayValue, delayUnit, maxDelayCount);
        return settings;
    }

    public void Update(int intervalValue, PatchingTimeUnit intervalUnit, int delayValue, PatchingTimeUnit delayUnit, int maxDelayCount)
    {
        Apply(intervalValue, intervalUnit, delayValue, delayUnit, maxDelayCount);
        MarkUpdated();
    }

    private void Apply(int intervalValue, PatchingTimeUnit intervalUnit, int delayValue, PatchingTimeUnit delayUnit, int maxDelayCount)
    {
        if (intervalValue < 1)
        {
            throw new DomainException("Patching interval must be at least 1.");
        }

        if (delayValue < 1)
        {
            throw new DomainException("Delay length must be at least 1.");
        }

        if (maxDelayCount < 0)
        {
            throw new DomainException("Maximum number of delays cannot be negative.");
        }

        IntervalValue = intervalValue;
        IntervalUnit = intervalUnit;
        DelayValue = delayValue;
        DelayUnit = delayUnit;
        MaxDelayCount = maxDelayCount;
    }
}
