using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.LogCollections;

/// <summary>Deletes collected logs (and finished or failed requests) 30 days after they were collected.</summary>
public class LogCollectionPruner
{
    private readonly ILogCollectionRepository _collections;
    private readonly IUnitOfWork _unitOfWork;

    public LogCollectionPruner(ILogCollectionRepository collections, IUnitOfWork unitOfWork)
    {
        _collections = collections;
        _unitOfWork = unitOfWork;
    }

    public Task<int> PruneAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        _collections.DeleteOlderThanAsync(now - Kintsugi.Domain.Entities.LogCollection.Retention, cancellationToken);
}
