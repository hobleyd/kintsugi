using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.Authentication.Commands.AddAdministrators;

public class AddAdministratorsCommandHandler : IRequestHandler<AddAdministratorsCommand, IReadOnlyList<AdministratorDto>>
{
    private readonly IAuthenticationSettingsRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public AddAdministratorsCommandHandler(IAuthenticationSettingsRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<IReadOnlyList<AdministratorDto>> Handle(AddAdministratorsCommand request, CancellationToken cancellationToken)
    {
        // The list lives on the sign-in settings, which cannot exist without a provider, client ID and
        // secret (AuthenticationSettings.Apply). Say so rather than inventing a half-configured row.
        var settings = await _repository.GetAsync(cancellationToken)
            ?? throw new DomainException("Configure a sign-in provider under Settings > Authentication before adding administrators.");

        settings.AddAdmins(request.Emails, request.AddedBy, DateTimeOffset.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return AdministratorDto.FromSettings(settings);
    }
}
