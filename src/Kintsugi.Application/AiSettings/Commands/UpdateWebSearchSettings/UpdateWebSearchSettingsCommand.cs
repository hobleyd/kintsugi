using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.AiSettings.Commands.UpdateWebSearchSettings;

/// <summary>Sets the search service behind Kintsugi's own <c>web_search</c> tool. A blank key keeps
/// the stored one; <see cref="ClearApiKey"/> removes it.</summary>
public record UpdateWebSearchSettingsCommand(WebSearchBackend Backend, string? ApiKey, bool ClearApiKey, string? BaseUrl)
    : IRequest<AiAgentSettingsDto>;

public class UpdateWebSearchSettingsCommandHandler : IRequestHandler<UpdateWebSearchSettingsCommand, AiAgentSettingsDto>
{
    private readonly IAiAgentSettingsRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateWebSearchSettingsCommandHandler(IAiAgentSettingsRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<AiAgentSettingsDto> Handle(UpdateWebSearchSettingsCommand request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);
        if (settings is null)
        {
            // Search can be set up before a provider is chosen: start from a disabled Routed row,
            // which needs no key and turns nothing on.
            settings = AiAgentSettings.Create(AiProvider.Routed, null, null, null, isEnabled: false);
            await _repository.AddAsync(settings, cancellationToken);
        }

        settings.UpdateWebSearch(request.Backend, request.ApiKey, request.ClearApiKey, request.BaseUrl);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return AiAgentSettingsDto.FromEntity(settings);
    }
}
