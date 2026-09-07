using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Auditing.Commands.UpdateAuditSettings;

public class UpdateAuditSettingsCommandHandler : IRequestHandler<UpdateAuditSettingsCommand, AuditSettingsDto>
{
    private readonly IAuditSettingsRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateAuditSettingsCommandHandler(IAuditSettingsRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<AuditSettingsDto> Handle(UpdateAuditSettingsCommand request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);

        if (settings is null)
        {
            settings = AuditSettings.Create(
                request.Provider,
                request.IsEnabled,
                request.Endpoint,
                request.Region,
                request.ClientId,
                request.Secret,
                request.TenantId,
                request.ProjectId,
                request.LogGroup,
                request.DataCollectionRuleId,
                request.Stream,
                request.Index);
            await _repository.AddAsync(settings, cancellationToken);
        }
        else
        {
            // Clearing first, as UpdateVantaSettingsCommandHandler does and for the same reason:
            // Update refuses a provider that requires a secret and has none, so the clear has to
            // have happened before that check runs or it would pass against the secret it is in the
            // middle of removing.
            if (request.ClearSecret)
            {
                settings.ClearSecret();
            }

            settings.Update(
                request.Provider,
                request.IsEnabled,
                request.Endpoint,
                request.Region,
                request.ClientId,
                request.Secret,
                request.TenantId,
                request.ProjectId,
                request.LogGroup,
                request.DataCollectionRuleId,
                request.Stream,
                request.Index);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return AuditSettingsDto.FromEntity(settings);
    }
}
