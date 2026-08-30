using MediatR;

namespace Kintsugi.Application.PatchingPolicy.Queries.GetPatchingPolicySettings;

public record GetPatchingPolicySettingsQuery : IRequest<PatchingPolicySettingsDto>;
