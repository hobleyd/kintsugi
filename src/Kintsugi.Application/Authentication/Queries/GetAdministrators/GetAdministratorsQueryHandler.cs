using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Authentication.Queries.GetAdministrators;

public class GetAdministratorsQueryHandler : IRequestHandler<GetAdministratorsQuery, IReadOnlyList<AdministratorDto>>
{
    private readonly IAuthenticationSettingsRepository _repository;

    public GetAdministratorsQueryHandler(IAuthenticationSettingsRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<AdministratorDto>> Handle(GetAdministratorsQuery request, CancellationToken cancellationToken) =>
        AdministratorDto.FromSettings(await _repository.GetAsync(cancellationToken));
}
