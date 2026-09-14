using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.ForcedPatchRuns.Commands.ClaimForcedPatchRuns;

public class ClaimForcedPatchRunsCommandHandler : IRequestHandler<ClaimForcedPatchRunsCommand, IReadOnlyList<ForcedPatchRunDto>>
{
    private readonly IHostRepository _hostRepository;
    private readonly IForcedPatchRunRepository _forcedPatchRunRepository;
    private readonly IUnitOfWork _unitOfWork;

    public ClaimForcedPatchRunsCommandHandler(
        IHostRepository hostRepository,
        IForcedPatchRunRepository forcedPatchRunRepository,
        IUnitOfWork unitOfWork)
    {
        _hostRepository = hostRepository;
        _forcedPatchRunRepository = forcedPatchRunRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<IReadOnlyList<ForcedPatchRunDto>> Handle(ClaimForcedPatchRunsCommand request, CancellationToken cancellationToken)
    {
        var host = await _hostRepository.GetBySerialNumberAsync(request.SerialNumber, cancellationToken);

        // An empty list rather than a 404. This route is polled once a minute for the life of the
        // agent process, and a host that is enrolled-but-not-yet-registered (or has just been
        // removed) answering 404 sixty times an hour would fill the log with a failure that is not
        // one: there is simply nothing to patch urgently.
        if (host is null)
        {
            return Array.Empty<ForcedPatchRunDto>();
        }

        var asOfUtc = DateTimeOffset.UtcNow;
        var collectable = await _forcedPatchRunRepository.GetCollectableForHostAsync(host.Id, asOfUtc, cancellationToken);

        if (collectable.Count == 0)
        {
            return Array.Empty<ForcedPatchRunDto>();
        }

        foreach (var run in collectable)
        {
            run.MarkCollected(asOfUtc);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return collectable
            .Select(run => new ForcedPatchRunDto(run.Id, run.ApplicationName, run.Platform, run.RequestedUtc))
            .ToList();
    }
}
