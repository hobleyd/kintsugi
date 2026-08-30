using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathScript;

public class GetUpgradePathScriptQueryHandler : IRequestHandler<GetUpgradePathScriptQuery, string?>
{
    private readonly IUpgradePathRepository _upgradePathRepository;

    public GetUpgradePathScriptQueryHandler(IUpgradePathRepository upgradePathRepository)
    {
        _upgradePathRepository = upgradePathRepository;
    }

    public async Task<string?> Handle(GetUpgradePathScriptQuery request, CancellationToken cancellationToken)
    {
        var path = await _upgradePathRepository.GetByApplicationIdentifierAsync(request.ApplicationIdentifier, cancellationToken);
        return path?.Script;
    }
}
