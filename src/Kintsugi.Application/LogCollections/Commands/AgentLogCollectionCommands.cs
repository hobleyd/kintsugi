using FluentValidation;
using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.HostActivity;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.LogCollections.Commands;

/// <summary>The log an agent read, gzip of JSON. Checked and rewritten by <see cref="LogPayload"/> before it is kept.</summary>
public record CompleteLogCollectionCommand(string SerialNumber, Guid CollectionId, byte[] Gzip) : IRequest<int>, IAgentScopedRequest;

public record FailLogCollectionCommand(string SerialNumber, Guid CollectionId, string Reason) : IRequest, IAgentScopedRequest;

public class FailLogCollectionCommandValidator : AbstractValidator<FailLogCollectionCommand>
{
    public FailLogCollectionCommandValidator()
    {
        RuleFor(x => x.SerialNumber).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(LogCollection.MaxFailureReasonLength * 2);
    }
}

public class CompleteLogCollectionCommandHandler : IRequestHandler<CompleteLogCollectionCommand, int>
{
    private readonly IHostRepository _hosts;
    private readonly ILogCollectionRepository _collections;
    private readonly IHostEventRecorder _events;
    private readonly IUnitOfWork _unitOfWork;

    public CompleteLogCollectionCommandHandler(IHostRepository hosts, ILogCollectionRepository collections, IHostEventRecorder events, IUnitOfWork unitOfWork)
    {
        _hosts = hosts;
        _collections = collections;
        _events = events;
        _unitOfWork = unitOfWork;
    }

    private static string Size(long bytes) => bytes < 1024 ? $"{bytes} bytes" : bytes < 1024 * 1024 ? $"{bytes / 1024} KB" : $"{bytes / 1024.0 / 1024.0:0.0} MB";

    /// <returns>How many entries were kept.</returns>
    public async Task<int> Handle(CompleteLogCollectionCommand request, CancellationToken cancellationToken)
    {
        var (host, collection) = await AgentLogCollectionLookup.FindAsync(_hosts, _collections, request.SerialNumber, request.CollectionId, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        // Before the upload is unzipped: Sanitise can expand up to 64 MB, and a finished collection must not cost that.
        collection.EnsureCanComplete();
        var sanitised = LogPayload.Sanitise(request.Gzip, collection.PeriodDays, now);
        collection.Complete(now, sanitised.Content, sanitised.Count, sanitised.TruncatedBefore);
        await _events.LogsCollectedAsync(host.Id, $"{sanitised.Count} entries, {Size(sanitised.Content.Length)}", now, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return sanitised.Count;
    }
}

public class FailLogCollectionCommandHandler : IRequestHandler<FailLogCollectionCommand>
{
    private readonly IHostRepository _hosts;
    private readonly ILogCollectionRepository _collections;
    private readonly IHostEventRecorder _events;
    private readonly IUnitOfWork _unitOfWork;

    public FailLogCollectionCommandHandler(IHostRepository hosts, ILogCollectionRepository collections, IHostEventRecorder events, IUnitOfWork unitOfWork)
    {
        _hosts = hosts;
        _collections = collections;
        _events = events;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(FailLogCollectionCommand request, CancellationToken cancellationToken)
    {
        var (host, collection) = await AgentLogCollectionLookup.FindAsync(_hosts, _collections, request.SerialNumber, request.CollectionId, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        collection.Fail(now, request.Reason);
        await _events.LogsFailedAsync(host.Id, collection.FailureReason!, now, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>An agent can only answer a collection that belongs to the host its certificate names: the collection id
/// alone is not enough, so a host cannot write another host's log.</summary>
internal static class AgentLogCollectionLookup
{
    public static async Task<(Host Host, LogCollection Collection)> FindAsync(
        IHostRepository hosts, ILogCollectionRepository collections, string serialNumber, Guid collectionId, CancellationToken cancellationToken)
    {
        var host = await hosts.GetBySerialNumberAsync(serialNumber, cancellationToken)
            ?? throw new NotFoundException($"No host is registered with serial number '{serialNumber}'.");
        var collection = await collections.GetByIdAsync(collectionId, cancellationToken);
        if (collection is null || collection.HostId != host.Id)
        {
            throw new NotFoundException("No such log collection for this host.");
        }

        return (host, collection);
    }
}
