using Moq;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Tests;

/// <summary>
/// A stand-in for the GitHub settings a consumer would read at runtime. Shared because every client
/// that talks to GitHub now reads through <see cref="IGitHubSettingsProvider"/> per call rather than
/// capturing configuration in its constructor — see <c>GitHubSettings</c>.
/// </summary>
public static class FakeGitHubSettings
{
    public const string DefaultRepository = "hobleyd/kintsugi";

    public static IGitHubSettingsProvider Provider(
        string? agentPackageRepository = null,
        string? scriptApprovalRepository = null,
        string? apiToken = null,
        string? scriptApprovalToken = null)
    {
        var snapshot = new GitHubSettingsSnapshot(
            agentPackageRepository ?? DefaultRepository,
            scriptApprovalRepository ?? DefaultRepository,
            apiToken,
            scriptApprovalToken);

        var provider = new Mock<IGitHubSettingsProvider>();
        provider.Setup(p => p.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        return provider.Object;
    }
}
