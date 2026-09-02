using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.UpgradePaths.Commands.TakeServerWrittenScript;

public class TakeServerWrittenScriptCommandHandler
    : IRequestHandler<TakeServerWrittenScriptCommand, TakeServerWrittenScriptResultDto>
{
    private readonly IUpgradePathRepository _upgradePathRepository;
    private readonly IUnitOfWork _unitOfWork;

    public TakeServerWrittenScriptCommandHandler(IUpgradePathRepository upgradePathRepository, IUnitOfWork unitOfWork)
    {
        _upgradePathRepository = upgradePathRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<TakeServerWrittenScriptResultDto> Handle(
        TakeServerWrittenScriptCommand request, CancellationToken cancellationToken)
    {
        var path = await _upgradePathRepository.GetAsync(request.ApplicationName, request.Platform, cancellationToken)
            ?? throw new NotFoundException(
                $"No upgrade path found for '{request.ApplicationName}' on '{request.Platform}'.");

        // Resolved from the row itself rather than taken from the request, so this can only ever
        // write the script that row's own bucket calls for — there is no parameter here that could
        // put a bash script on a Windows row, which is the failure the per-manager buckets exist to
        // prevent.
        var script = PackageManagerCatalog.CurrentScriptFor(path.ApplicationName, path.Platform)
            ?? throw new DomainException(
                $"'{path.ApplicationName}' on '{path.Platform}' is not a recognized package manager's row, so this "
                + "server writes no script for it — an AI-researched script has no newer server-written version to "
                + "take. Use \"Find Upgrade Paths\" on the Applications page to re-research one.");

        if (string.Equals(path.Script, script, StringComparison.Ordinal))
        {
            return new TakeServerWrittenScriptResultDto(path.ApplicationName, path.Platform, Changed: false);
        }

        path.TakeServerWrittenScript(script);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new TakeServerWrittenScriptResultDto(path.ApplicationName, path.Platform, Changed: true);
    }
}
