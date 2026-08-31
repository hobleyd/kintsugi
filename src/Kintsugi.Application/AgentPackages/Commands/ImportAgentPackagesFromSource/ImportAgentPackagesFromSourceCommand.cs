using MediatR;

namespace Kintsugi.Application.AgentPackages.Commands.ImportAgentPackagesFromSource;

/// <summary>
/// Pulls every platform's newest upstream build down, points it at this server, and publishes it
/// locally — what the Clients page's "Refresh clients" button runs.
///
/// <paramref name="ApiBaseUrl"/> is the address agents will be told to call home on, substituted
/// into each archive's bundled <c>config.toml</c>. It is derived from the address the Clients page
/// itself was reached on (see <c>ClientsModel</c>), which is safe because nginx 301s the plain-HTTP
/// listener to the TLS one — so a request that reached the page came in over TLS on the port
/// agents also use, and there is no way to reach it over a scheme mutual TLS could not work on.
/// </summary>
public record ImportAgentPackagesFromSourceCommand(string ApiBaseUrl) : IRequest<IReadOnlyList<AgentPackageImportResultDto>>;

/// <summary>What happened to one platform during a refresh.</summary>
public enum AgentPackageImportOutcome
{
    /// <summary>Downloaded, rewritten and published — this platform now has a new build.</summary>
    Imported,

    /// <summary>This exact (platform, version) was already published here; nothing to do.</summary>
    AlreadyPublished,

    /// <summary>The download, rewrite or publish failed. The other platforms are unaffected —
    /// a refresh imports what it can rather than rolling everything back.</summary>
    Failed
}

public record AgentPackageImportResultDto(
    string Platform,
    string Version,
    AgentPackageImportOutcome Outcome,
    string? Message);
