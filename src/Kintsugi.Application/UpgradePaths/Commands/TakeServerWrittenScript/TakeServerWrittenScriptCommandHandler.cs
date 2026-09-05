using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ScriptApproval;
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
        var rows = (await _upgradePathRepository.GetScriptUpgradePathsAsync(request.Platform, cancellationToken))
            .Where(r => r.Script is not null
                && string.Equals(ScriptContentHash.Of(r.Script), request.Sha256, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (rows.Count == 0)
        {
            throw new NotFoundException(
                $"No upgrade path on '{request.Platform}' holds a script with content hash '{request.Sha256}'. "
                + "The page may be stale — reload it.");
        }

        var changed = 0;
        foreach (var path in rows)
        {
            // Resolved from each row itself rather than taken from the request, so this can only ever
            // write the script that row's own bucket and name call for — there is no parameter here
            // that could put a bash script on a Windows row, or the per-application script on the
            // manager's own self-update row. The first is the failure the per-manager buckets exist to
            // prevent; the second is why this is per row inside the group rather than one text for all.
            var script = PackageManagerCatalog.CurrentScriptFor(path.ApplicationName, path.Platform)
                ?? throw new DomainException(
                    $"'{path.ApplicationName}' on '{path.Platform}' is not a recognized package manager's row, so this "
                    + "server writes no script for it — an AI-researched script has no newer server-written version to "
                    + "take. Use \"Find Upgrade Paths\" on the Applications page to re-research one.");

            if (string.Equals(path.Script, script, StringComparison.Ordinal))
            {
                continue;
            }

            path.TakeServerWrittenScript(script);
            changed++;
        }

        if (changed > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new TakeServerWrittenScriptResultDto(request.Platform, changed);
    }
}
