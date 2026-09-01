using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.GitHub.Queries.GetGitHubSettings;

public class GetGitHubSettingsQueryHandler : IRequestHandler<GetGitHubSettingsQuery, GitHubSettingsDto>
{
    private readonly IGitHubSettingsRepository _repository;
    private readonly IGitHubSettingsProvider _provider;

    public GetGitHubSettingsQueryHandler(IGitHubSettingsRepository repository, IGitHubSettingsProvider provider)
    {
        _repository = repository;
        _provider = provider;
    }

    public async Task<GitHubSettingsDto> Handle(GetGitHubSettingsQuery request, CancellationToken cancellationToken)
    {
        // Both, because they answer different questions: the provider gives the effective values the
        // rest of the system will actually use, and the stored row is what says whether a value was
        // chosen or merely defaulted. The page needs to distinguish those to be honest about which
        // repository it is really pointed at.
        var stored = await _repository.GetAsync(cancellationToken);
        var effective = await _provider.GetAsync(cancellationToken);

        return new GitHubSettingsDto(
            effective.AgentPackageRepository,
            string.IsNullOrWhiteSpace(stored?.AgentPackageRepository),
            effective.ScriptApprovalRepository,
            string.IsNullOrWhiteSpace(stored?.ScriptApprovalRepository),
            !string.IsNullOrWhiteSpace(stored?.ApiToken),
            !string.IsNullOrWhiteSpace(stored?.ScriptApprovalToken));
    }
}
