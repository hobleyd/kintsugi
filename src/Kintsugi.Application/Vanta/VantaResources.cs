namespace Kintsugi.Application.Vanta;

/// <summary>
/// One record in Vanta's <c>VulnerableComponent</c> resource — a system component that
/// vulnerabilities are reported against. Kintsugi maps one of these to each managed host.
/// </summary>
/// <remarks>
/// Field names and their required-ness come straight from
/// <c>PUT /v1/resources/vulnerable_component</c> in
/// https://developer.vanta.com/reference/build-integrations.json. Every property here is required by
/// that schema, which is why none of them is nullable: a record that cannot be filled honestly is
/// not built at all (see <see cref="VantaResourceBuilder"/>, which drops a host that has never
/// checked in rather than inventing a <see cref="CollectedTimestamp"/> for it).
/// </remarks>
public record VantaVulnerableComponent(
    string DisplayName,
    string UniqueId,
    string ExternalUrl,
    DateTimeOffset CollectedTimestamp,
    string Name,
    string Description,
    string TargetType);

/// <summary>
/// One record in Vanta's <c>PackageVulnerabilityConnectors</c> resource — a vulnerable package on a
/// component. Kintsugi maps one of these to each out-of-date application, and one per host to a
/// pending operating-system update.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="VulnerableComponentUniqueId"/> must equal the <see cref="VantaVulnerableComponent.UniqueId"/>
/// of a component Vanta already holds, which is why the two syncs are ordered and why a failed
/// component sync cancels the package sync outright — see <c>SyncVantaResourcesCommandHandler</c>.
/// </para>
/// <para>
/// The spec's optional <c>cveId</c>, <c>cvss3Score</c>, <c>cvss3Vector</c> and <c>isReachable</c>
/// fields are deliberately absent from this record rather than nullable. Kintsugi compares installed
/// versions against latest known versions; it has no CVE feed, no CVSS vector and no reachability
/// analysis, so there is no value it could put there that would not be a guess presented as a
/// finding. See <see cref="Domain.Entities.VantaSettings.Severity"/> for the same reasoning applied
/// to the one such field Vanta makes mandatory.
/// </para>
/// </remarks>
public record VantaPackageVulnerability(
    string DisplayName,
    string UniqueId,
    string ExternalUrl,
    string PackageName,
    string PackageVersion,
    double Severity,
    string VulnerableComponentUniqueId,
    string Description,
    bool IsResolvable,
    string RemediationInstructions);

/// <summary>
/// The complete state of the world this server would send Vanta right now: every component, and
/// every package vulnerability across all of them.
/// </summary>
/// <remarks>
/// Deliberately one object holding both halves, built in full before anything is sent. Each Vanta
/// sync endpoint is a state-of-the-world replacement — anything previously sent and now omitted is
/// deleted on Vanta's side — so there is no such thing as a partial or chunked upload here, and a
/// builder that streamed would be a builder that could delete half the inventory on an exception
/// halfway through.
/// </remarks>
public record VantaFleetSnapshot(
    IReadOnlyList<VantaVulnerableComponent> Components,
    IReadOnlyList<VantaPackageVulnerability> Packages);
