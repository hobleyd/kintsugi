using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.LogCollections.Commands;

/// <summary>An agent asking, at its check-in, whether anybody wants its log. Reading consumes: the request moves to
/// "Collecting" in the same call, so the next check-in does not read the log twice.</summary>
public record ClaimLogCollectionRequestsCommand(string SerialNumber) : IRequest<IReadOnlyList<LogCollectionRequestDto>>, IAgentScopedRequest;

public class ClaimLogCollectionRequestsCommandHandler : IRequestHandler<ClaimLogCollectionRequestsCommand, IReadOnlyList<LogCollectionRequestDto>>
{
    private readonly IHostRepository _hosts;
    private readonly ILogCollectionRepository _collections;
    private readonly IUnitOfWork _unitOfWork;

    public ClaimLogCollectionRequestsCommandHandler(IHostRepository hosts, ILogCollectionRepository collections, IUnitOfWork unitOfWork)
    {
        _hosts = hosts;
        _collections = collections;
        _unitOfWork = unitOfWork;
    }

    public async Task<IReadOnlyList<LogCollectionRequestDto>> Handle(ClaimLogCollectionRequestsCommand request, CancellationToken cancellationToken)
    {
        // An empty list rather than a 404, as for forced patch runs: this is asked at every check-in, and a host
        // not registered yet has simply nothing waiting.
        var host = await _hosts.GetBySerialNumberAsync(request.SerialNumber, cancellationToken);
        if (host is null)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var collection = await _collections.GetCollectableForHostAsync(host.Id, now, cancellationToken);
        if (collection is null)
        {
            return [];
        }

        collection.MarkClaimed(now);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return [new LogCollectionRequestDto(collection.Id, collection.PeriodDays, collection.Detail.ToString())];
    }
}
