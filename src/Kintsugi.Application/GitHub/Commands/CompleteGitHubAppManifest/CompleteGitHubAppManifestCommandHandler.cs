using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.GitHub.Commands.CompleteGitHubAppManifest;

public class CompleteGitHubAppManifestCommandHandler : IRequestHandler<CompleteGitHubAppManifestCommand, string>
{
    private readonly IGitHubSettingsRepository _repository;
    private readonly IGitHubAppClient _gitHubAppClient;
    private readonly IUnitOfWork _unitOfWork;

    public CompleteGitHubAppManifestCommandHandler(
        IGitHubSettingsRepository repository, IGitHubAppClient gitHubAppClient, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _gitHubAppClient = gitHubAppClient;
        _unitOfWork = unitOfWork;
    }

    public async Task<string> Handle(CompleteGitHubAppManifestCommand request, CancellationToken cancellationToken)
    {
        // Converted before anything is read or written here: the code is single-use, so if this
        // fails there is nothing to roll back, and if it succeeds the private key exists nowhere but
        // in this response — it has to reach the database in this same request.
        var credentials = await _gitHubAppClient.ConvertManifestAsync(request.Code.Trim(), cancellationToken);

        var settings = await _repository.GetAsync(cancellationToken);
        if (settings is null)
        {
            settings = Domain.Entities.GitHubSettings.Create(null, null, null, null);
            await _repository.AddAsync(settings, cancellationToken);
        }

        settings.ConnectGitHubApp(credentials.AppId, credentials.Slug, credentials.Owner, credentials.PrivateKeyPem);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return credentials.Slug;
    }
}
