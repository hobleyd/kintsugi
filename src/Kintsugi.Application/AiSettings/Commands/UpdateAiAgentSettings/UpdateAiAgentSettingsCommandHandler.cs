using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.AiSettings.Commands.UpdateAiAgentSettings;

public class UpdateAiAgentSettingsCommandHandler : IRequestHandler<UpdateAiAgentSettingsCommand, AiAgentSettingsDto>
{
    private readonly IAiAgentSettingsRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateAiAgentSettingsCommandHandler(IAiAgentSettingsRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<AiAgentSettingsDto> Handle(UpdateAiAgentSettingsCommand request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);

        if (settings is null)
        {
            settings = AiAgentSettings.Create(request.Provider, request.ApiKey, request.BaseUrl, request.Model, request.IsEnabled);
            await _repository.AddAsync(settings, cancellationToken);
        }
        else
        {
            settings.Update(request.Provider, request.ApiKey, request.BaseUrl, request.Model, request.IsEnabled);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return AiAgentSettingsDto.FromEntity(settings);
    }
}
