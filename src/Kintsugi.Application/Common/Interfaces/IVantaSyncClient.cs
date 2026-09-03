using Kintsugi.Application.Vanta;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Pushes resource collections into Vanta's "Build integrations" API.
/// </summary>
/// <remarks>
/// <para>
/// Two methods rather than one, because Vanta registers each resource type separately (each has its
/// own resource ID and its own endpoint) and because the order matters: a package vulnerability
/// names the component it belongs to by <c>uniqueId</c>, so components must land first. The caller
/// enforces that ordering — see <c>SyncVantaResourcesCommandHandler</c> — rather than this
/// interface hiding it, because the interesting half is what happens when the first call fails.
/// </para>
/// <para>
/// Each call is a complete replacement of everything this app previously synced for that resource:
/// anything omitted is deleted on Vanta's side. There is deliberately no "append" or "upsert one"
/// method, because Vanta offers none and a caller that thought it had one would silently empty the
/// inventory.
/// </para>
/// <para>
/// Throws <see cref="Exceptions.ExternalServiceException"/> on any failure — an unconfigured
/// integration, a rejected token, or a non-success response.
/// </para>
/// </remarks>
public interface IVantaSyncClient
{
    Task SyncVulnerableComponentsAsync(IReadOnlyList<VantaVulnerableComponent> components, CancellationToken cancellationToken);

    Task SyncPackageVulnerabilitiesAsync(IReadOnlyList<VantaPackageVulnerability> packages, CancellationToken cancellationToken);
}
