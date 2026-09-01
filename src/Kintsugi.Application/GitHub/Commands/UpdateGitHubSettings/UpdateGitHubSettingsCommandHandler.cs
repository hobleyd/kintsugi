using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.GitHub.Queries.GetGitHubSettings;

namespace Kintsugi.Application.GitHub.Commands.UpdateGitHubSettings;

public class UpdateGitHubSettingsCommandHandler : IRequestHandler<UpdateGitHubSettingsCommand, GitHubSettingsDto>
{
    private readonly IGitHubSettingsRepository _repository;
    private readonly ISender _sender;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateGitHubSettingsCommandHandler(
        IGitHubSettingsRepository repository, ISender sender, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _sender = sender;
        _unitOfWork = unitOfWork;
    }

    public async Task<GitHubSettingsDto> Handle(UpdateGitHubSettingsCommand request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);

        if (settings is null)
        {
            settings = Domain.Entities.GitHubSettings.Create(
                request.ApiToken, request.AgentPackageRepository, request.ScriptApprovalRepository, request.ScriptApprovalToken);
            await _repository.AddAsync(settings, cancellationToken);
        }
        else
        {
            settings.Update(
                request.ApiToken, request.AgentPackageRepository, request.ScriptApprovalRepository, request.ScriptApprovalToken);
        }

        // After Update, not before: Update treats a blank token as "keep", so clearing has to be the
        // last word or a save that both clears and leaves the box empty would keep the old token.
        if (request.ClearApiToken)
        {
            settings.ClearApiToken();
        }

        if (request.ClearScriptApprovalToken)
        {
            settings.ClearScriptApprovalToken();
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await _sender.Send(new GetGitHubSettingsQuery(), cancellationToken);
    }
}
