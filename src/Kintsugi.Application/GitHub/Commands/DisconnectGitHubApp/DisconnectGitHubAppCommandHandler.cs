using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.GitHub.Queries.GetGitHubSettings;

namespace Kintsugi.Application.GitHub.Commands.DisconnectGitHubApp;

public class DisconnectGitHubAppCommandHandler : IRequestHandler<DisconnectGitHubAppCommand, GitHubSettingsDto>
{
    private readonly IGitHubSettingsRepository _repository;
    private readonly ISender _sender;
    private readonly IUnitOfWork _unitOfWork;

    public DisconnectGitHubAppCommandHandler(IGitHubSettingsRepository repository, ISender sender, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _sender = sender;
        _unitOfWork = unitOfWork;
    }

    public async Task<GitHubSettingsDto> Handle(DisconnectGitHubAppCommand request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);
        if (settings is not null && settings.HasGitHubApp)
        {
            settings.DisconnectGitHubApp();
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await _sender.Send(new GetGitHubSettingsQuery(), cancellationToken);
    }
}
