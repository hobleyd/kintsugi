using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathRefreshStatus;

public record GetUpgradePathRefreshStatusQuery(string ApplicationName) : IRequest<UpgradePathRefreshStatusDto>;
