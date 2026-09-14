using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.ForcedPatchRuns.Commands.RequestForcedPatchRuns;

public class RequestForcedPatchRunsCommandHandler : IRequestHandler<RequestForcedPatchRunsCommand, RequestForcedPatchRunsResult>
{
    private readonly IHostRepository _hostRepository;
    private readonly IForcedPatchRunRepository _forcedPatchRunRepository;
    private readonly IUnitOfWork _unitOfWork;

    public RequestForcedPatchRunsCommandHandler(
        IHostRepository hostRepository,
        IForcedPatchRunRepository forcedPatchRunRepository,
        IUnitOfWork unitOfWork)
    {
        _hostRepository = hostRepository;
        _forcedPatchRunRepository = forcedPatchRunRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<RequestForcedPatchRunsResult> Handle(RequestForcedPatchRunsCommand request, CancellationToken cancellationToken)
    {
        // One read of the fleet rather than a lookup per name, and GetAllAsync is the right one of
        // the two: it already excludes a host whose removal has been requested, which is exactly
        // the host that must not be told to patch — the agent is on its way out and the row would
        // outlive the machine. A name that is not in it comes back as NotRequested.
        var hosts = await _hostRepository.GetAllAsync(cancellationToken);
        var byName = hosts
            .GroupBy(h => h.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var requestedUtc = DateTimeOffset.UtcNow;
        var requested = 0;
        var notRequested = new List<string>();

        // Distinct, because the caller's filter can name the same host through two rows and two
        // instructions for one machine are one instruction.
        foreach (var hostName in request.HostNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!byName.TryGetValue(hostName, out var host))
            {
                notRequested.Add(hostName);
                continue;
            }

            var existing = await _forcedPatchRunRepository.GetOutstandingAsync(
                host.Id, request.ApplicationName, request.Platform, cancellationToken);

            if (existing is not null)
            {
                // Already waiting to be collected: push its clock forward rather than opening a
                // second row the agent would find on the poll after the one it patches on.
                existing.Renew(requestedUtc, ForcedPatchRun.DefaultLifetime);
            }
            else
            {
                await _forcedPatchRunRepository.AddAsync(
                    ForcedPatchRun.Request(host.Id, request.ApplicationName, request.Platform, requestedUtc, ForcedPatchRun.DefaultLifetime),
                    cancellationToken);
            }

            requested++;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new RequestForcedPatchRunsResult(requested, notRequested);
    }
}
