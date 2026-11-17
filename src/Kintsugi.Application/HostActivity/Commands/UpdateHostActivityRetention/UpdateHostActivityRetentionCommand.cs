using MediatR;

namespace Kintsugi.Application.HostActivity.Commands.UpdateHostActivityRetention;

/// <summary>Sets how many months of host activity are kept.</summary>
public record UpdateHostActivityRetentionCommand(int RetentionMonths) : IRequest<HostActivitySettingsDto>;
