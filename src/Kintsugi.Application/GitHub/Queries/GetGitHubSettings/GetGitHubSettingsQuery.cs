using MediatR;

namespace Kintsugi.Application.GitHub.Queries.GetGitHubSettings;

public record GetGitHubSettingsQuery : IRequest<GitHubSettingsDto>;
