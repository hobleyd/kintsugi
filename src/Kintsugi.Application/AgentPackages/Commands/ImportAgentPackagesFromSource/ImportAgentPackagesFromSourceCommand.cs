using MediatR;

namespace Kintsugi.Application.AgentPackages.Commands.ImportAgentPackagesFromSource;

/// <summary>
/// Pulls every platform's newest upstream build down, points it at this server, and publishes it
/// locally — what the Clients screen's "Refresh clients" button runs.
///
/// <paramref name="ApiBaseUrl"/> is the address agents will be told to call home on, substituted
/// into each archive's bundled <c>config.toml</c>. It is resolved by
/// <c>AdminClientsController.ResolveAgentApiBaseUrl</c>: <c>AGENT_API_BASE_URL</c> when set, and
/// otherwise the address the request arrived on — with the client saying out loud that it has
/// guessed. Do not restore the earlier reasoning that deriving it is simply safe because nginx 301s
/// its plain-HTTP listener to the TLS one. That covers the scheme and the port and misses the front
/// door: nginx is what verifies the agent's client certificate, so any TLS-terminating hop in front
/// of it ends the handshake at itself, and the admin UI's address is then the wrong answer. It
/// shipped agents that enrolled cleanly and then 403'd on every authenticated route.
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
