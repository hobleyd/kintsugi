using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.PatchingPolicy;

public record PatchingPolicySettingsDto(
    int IntervalValue,
    PatchingTimeUnit IntervalUnit,
    int DelayValue,
    PatchingTimeUnit DelayUnit,
    int MaxDelayCount)
{
    public static PatchingPolicySettingsDto FromEntity(PatchingPolicySettings entity) =>
        new(entity.IntervalValue, entity.IntervalUnit, entity.DelayValue, entity.DelayUnit, entity.MaxDelayCount);

    // Sensible defaults shown the first time this page loads, before anything has been saved:
    // patch weekly, allow deferring a required restart/reboot by a day at a time, up to 3 times.
    public static PatchingPolicySettingsDto Default() =>
        new(IntervalValue: 7, IntervalUnit: PatchingTimeUnit.Days, DelayValue: 1, DelayUnit: PatchingTimeUnit.Days, MaxDelayCount: 3);
}
