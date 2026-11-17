using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Authentication.Commands.RemoveAdministrator;

public class RemoveAdministratorCommandHandler : IRequestHandler<RemoveAdministratorCommand, IReadOnlyList<AdministratorDto>>
{
    private readonly IAuthenticationSettingsRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public RemoveAdministratorCommandHandler(IAuthenticationSettingsRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<IReadOnlyList<AdministratorDto>> Handle(RemoveAdministratorCommand request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);
        if (settings is null)
        {
            return AdministratorDto.FromSettings(null);
        }

        settings.RemoveAdmin(request.Email);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return AdministratorDto.FromSettings(settings);
    }
}
