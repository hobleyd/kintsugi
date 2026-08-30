using MediatR;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathScript;

/// <summary>
/// Looks up the generated script served as "{ApplicationIdentifier}.sh" — backs
/// <c>GET /api/upgrade-paths/scripts/{appId}</c>, the addressable-by-appId serving model an agent
/// (or anything else) fetches a script from directly, rather than it only being reachable embedded
/// inside a <see cref="UpgradeStatusDto"/>.
/// </summary>
public record GetUpgradePathScriptQuery(string ApplicationIdentifier) : IRequest<string?>;
