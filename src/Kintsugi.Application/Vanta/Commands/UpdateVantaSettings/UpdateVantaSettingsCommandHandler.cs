using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Vanta.Queries.GetVantaSettings;

namespace Kintsugi.Application.Vanta.Commands.UpdateVantaSettings;

public class UpdateVantaSettingsCommandHandler : IRequestHandler<UpdateVantaSettingsCommand, VantaSettingsDto>
{
    private readonly IVantaSettingsRepository _repository;
    private readonly ISender _sender;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateVantaSettingsCommandHandler(
        IVantaSettingsRepository repository, ISender sender, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _sender = sender;
        _unitOfWork = unitOfWork;
    }

    public async Task<VantaSettingsDto> Handle(UpdateVantaSettingsCommand request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);

        if (settings is null)
        {
            settings = Domain.Entities.VantaSettings.Create(
                request.Enabled,
                request.ClientId,
                request.ClientSecret,
                request.ApiBaseUrl,
                request.VulnerableComponentResourceId,
                request.PackageVulnerabilityResourceId,
                request.ConsoleBaseUrl,
                request.Severity,
                request.SyncIntervalHours);
            await _repository.AddAsync(settings, cancellationToken);
        }
        else
        {
            // Clearing first, unlike UpdateGitHubSettingsCommandHandler, which clears last. The
            // difference is that Update here can *reject* the save outright — enabling without a
            // secret is a domain error — so the secret has to be gone before that check runs, or
            // "clear the secret and switch the integration off" would be validated against the
            // secret it is in the middle of removing and pass for the wrong reason.
            if (request.ClearClientSecret)
            {
                settings.ClearClientSecret();
            }

            settings.Update(
                request.Enabled,
                request.ClientId,
                request.ClientSecret,
                request.ApiBaseUrl,
                request.VulnerableComponentResourceId,
                request.PackageVulnerabilityResourceId,
                request.ConsoleBaseUrl,
                request.Severity,
                request.SyncIntervalHours);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await _sender.Send(new GetVantaSettingsQuery(), cancellationToken);
    }
}
