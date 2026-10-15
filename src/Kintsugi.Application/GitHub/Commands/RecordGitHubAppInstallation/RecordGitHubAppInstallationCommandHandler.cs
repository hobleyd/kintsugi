using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.GitHub.Queries.GetGitHubSettings;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.GitHub.Commands.RecordGitHubAppInstallation;

public class RecordGitHubAppInstallationCommandHandler
    : IRequestHandler<RecordGitHubAppInstallationCommand, GitHubSettingsDto>
{
    private readonly IGitHubSettingsRepository _repository;
    private readonly IGitHubAppClient _gitHubAppClient;
    private readonly ISender _sender;
    private readonly IUnitOfWork _unitOfWork;

    public RecordGitHubAppInstallationCommandHandler(
        IGitHubSettingsRepository repository, IGitHubAppClient gitHubAppClient, ISender sender, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _gitHubAppClient = gitHubAppClient;
        _sender = sender;
        _unitOfWork = unitOfWork;
    }

    public async Task<GitHubSettingsDto> Handle(RecordGitHubAppInstallationCommand request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);
        if (settings is null || !settings.HasGitHubApp)
        {
            throw new DomainException("No GitHub App is connected. Create one from Settings > GitHub first.");
        }

        // Throws unless GitHub confirms the installation belongs to this App. Without this, anyone
        // able to send an administrator a link could point the stored id at an installation of their
        // choosing — and while minting for it would then fail, the failure would surface hours later
        // as approvals that quietly stopped publishing.
        await _gitHubAppClient.GetInstallationAccountAsync(
            settings.GitHubAppId!.Value, settings.GitHubAppPrivateKey!, request.InstallationId, cancellationToken);

        settings.RecordGitHubAppInstallation(request.InstallationId);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await _sender.Send(new GetGitHubSettingsQuery(), cancellationToken);
    }
}
