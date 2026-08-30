using MediatR;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.PatchingPolicy.Commands.UpdatePatchingPolicySettings;

public record UpdatePatchingPolicySettingsCommand(
    int IntervalValue,
    PatchingTimeUnit IntervalUnit,
    int DelayValue,
    PatchingTimeUnit DelayUnit,
    int MaxDelayCount) : IRequest<PatchingPolicySettingsDto>;
