using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.PatchingPolicy.Queries.GetPatchingPolicySettings;

public class GetPatchingPolicySettingsQueryHandler : IRequestHandler<GetPatchingPolicySettingsQuery, PatchingPolicySettingsDto>
{
    private readonly IPatchingPolicySettingsRepository _repository;

    public GetPatchingPolicySettingsQueryHandler(IPatchingPolicySettingsRepository repository)
    {
        _repository = repository;
    }

    public async Task<PatchingPolicySettingsDto> Handle(GetPatchingPolicySettingsQuery request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);
        return settings is null ? PatchingPolicySettingsDto.Default() : PatchingPolicySettingsDto.FromEntity(settings);
    }
}
