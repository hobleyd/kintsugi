namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// The address agents are told to call home on, baked into every imported package's bundled
/// <c>config.toml</c> (see <c>ImportAgentPackagesFromSourceCommandHandler</c>).
///
/// This exists as explicit configuration because it cannot be derived from the address the admin
/// UI was reached on, and the failure when it is derived wrongly is quiet and total. Enrollment
/// (<c>/api/host/enroll</c>) is deliberately outside nginx's client-certificate regex, so an agent
/// pointed at the wrong front door still enrolls successfully and only then finds that every
/// authenticated route answers 403 — the agent looks installed, and reports nothing, forever.
///
/// The address must be one where the agent's client certificate survives all the way to nginx,
/// which is what performs the verification. Anything that terminates TLS in front of nginx — a
/// gateway, a load balancer, a CDN — ends the mutual-TLS handshake at itself and cannot forward
/// the certificate, so the admin UI's own address is frequently the wrong answer. Deployment
/// detail, so it lives in <c>.env</c> alongside <see cref="IAgentEnrollmentOptions"/>'s token and
/// nginx's TLS material rather than in a tracked file.
/// </summary>
public interface IAgentApiOptions
{
    /// <summary>The configured agent-facing base URL, or null when <c>AGENT_API_BASE_URL</c> is
    /// unset — in which case callers fall back to the address the request arrived on and should
    /// say on-screen that they have done so.</summary>
    string? AgentApiBaseUrl { get; }
}
