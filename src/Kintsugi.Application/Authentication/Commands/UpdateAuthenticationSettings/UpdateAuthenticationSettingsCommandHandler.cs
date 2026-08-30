using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Authentication.Commands.UpdateAuthenticationSettings;

public class UpdateAuthenticationSettingsCommandHandler : IRequestHandler<UpdateAuthenticationSettingsCommand, AuthenticationSettingsDto>
{
    private readonly IAuthenticationSettingsRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateAuthenticationSettingsCommandHandler(IAuthenticationSettingsRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<AuthenticationSettingsDto> Handle(UpdateAuthenticationSettingsCommand request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);

        if (settings is null)
        {
            settings = AuthenticationSettings.Create(
                request.Provider, request.ClientId, request.ClientSecret, request.Authority, request.TenantId, request.HostedDomain, request.IsEnabled);
            await _repository.AddAsync(settings, cancellationToken);
        }
        else
        {
            settings.Update(
                request.Provider, request.ClientId, request.ClientSecret, request.Authority, request.TenantId, request.HostedDomain, request.IsEnabled);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return AuthenticationSettingsDto.FromEntity(settings);
    }
}
