using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Authentication.Queries.GetAuthenticationSettings;

public class GetAuthenticationSettingsQueryHandler : IRequestHandler<GetAuthenticationSettingsQuery, AuthenticationSettingsDto>
{
    private readonly IAuthenticationSettingsRepository _repository;

    public GetAuthenticationSettingsQueryHandler(IAuthenticationSettingsRepository repository)
    {
        _repository = repository;
    }

    public async Task<AuthenticationSettingsDto> Handle(GetAuthenticationSettingsQuery request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);
        return settings is null ? AuthenticationSettingsDto.NotConfigured() : AuthenticationSettingsDto.FromEntity(settings);
    }
}
