using MediatR;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradeStatuses;

/// <summary>
/// Lists one host's installed applications alongside their latest known upgrade path — this is
/// how the kintsugi-agent asks "what upgrades are available for what's installed on me, and how
/// do I apply them?" Scoped to a single host by design: across a large fleet, an unscoped listing
/// would mean one row per (host, application) pair, which doesn't stay cheap. The Applications
/// page's own fleet-wide view uses <c>GetUpgradePathSummariesQuery</c> instead, which aggregates
/// host counts rather than listing them.
/// </summary>
public record GetUpgradeStatusesQuery(string SerialNumber) : IRequest<IReadOnlyList<UpgradeStatusDto>>;
