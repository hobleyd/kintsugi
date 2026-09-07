using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AgentPackages.Queries.GetWindowsBootstrapScript;

public class GetWindowsBootstrapScriptQueryHandler
    : IRequestHandler<GetWindowsBootstrapScriptQuery, WindowsBootstrapScriptDto>
{
    /// <summary>The agent-package platform namespace, which is deliberately not
    /// <c>PlatformBucket</c>'s — see <c>IAgentPackageSourceClient</c>.</summary>
    private const string WindowsPlatform = "windows";

    private readonly IAgentPackageRepository _repository;
    private readonly IAgentEnrollmentOptions _enrollmentOptions;

    public GetWindowsBootstrapScriptQueryHandler(
        IAgentPackageRepository repository,
        IAgentEnrollmentOptions enrollmentOptions)
    {
        _repository = repository;
        _enrollmentOptions = enrollmentOptions;
    }

    public async Task<WindowsBootstrapScriptDto> Handle(
        GetWindowsBootstrapScriptQuery request,
        CancellationToken cancellationToken)
    {
        var package = await _repository.GetLatestByPlatformAsync(WindowsPlatform, cancellationToken);
        if (package is null)
        {
            return new WindowsBootstrapScriptDto(
                Script: null,
                Version: null,
                Sha256: null,
                UnavailableReason: "No Windows agent package has been published on this server yet. "
                    + "Press \"Refresh clients\" to import the newest build.");
        }

        if (package.UpstreamSha256 is null)
        {
            // Reported rather than thrown: this is the ordinary state of a server that imported its
            // Windows package before the upstream checksum was recorded, and the remedy is a button
            // on the same screen. See AgentPackage.UpstreamSha256.
            return new WindowsBootstrapScriptDto(
                Script: null,
                Version: package.Version,
                Sha256: null,
                UnavailableReason: WindowsBootstrapScript.NoUpstreamProvenanceReason);
        }

        // The current token, read at render time rather than captured, exactly as the archive
        // rewriter reads it on every download — a rotation must never leave a rendered script
        // carrying a token that no longer enrolls anything.
        var script = WindowsBootstrapScript.Build(package, request.ApiBaseUrl, _enrollmentOptions.EnrollmentToken);

        return new WindowsBootstrapScriptDto(script, package.Version, package.UpstreamSha256, UnavailableReason: null);
    }
}
