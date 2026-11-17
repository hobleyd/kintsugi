using System.IO.Compression;
using System.Text;
using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.HostActivity;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.LogCollections.Queries;

public record GetLatestLogCollectionQuery(Guid HostId) : IRequest<LogCollectionDto?>;

public class GetLatestLogCollectionQueryHandler : IRequestHandler<GetLatestLogCollectionQuery, LogCollectionDto?>
{
    private readonly ILogCollectionRepository _collections;

    public GetLatestLogCollectionQueryHandler(ILogCollectionRepository collections) => _collections = collections;

    public async Task<LogCollectionDto?> Handle(GetLatestLogCollectionQuery request, CancellationToken cancellationToken) =>
        (await _collections.GetLatestSummaryForHostAsync(request.HostId, cancellationToken))?.ToDto(DateTimeOffset.UtcNow);
}

/// <param name="Severity">"all", "errors" (critical and error) or "warnings".</param>
/// <param name="AroundUtc">With <c>AroundMinutes</c>: only entries that close to this moment.</param>
public record GetLogEntriesQuery(
    Guid HostId, Guid CollectionId, string? Severity, string? Search, DateTimeOffset? AroundUtc, int AroundMinutes, int Skip, int Take)
    : IRequest<LogEntriesPageDto>;

public class GetLogEntriesQueryHandler : IRequestHandler<GetLogEntriesQuery, LogEntriesPageDto>
{
    public const int MaxTake = 500;

    private readonly ILogCollectionRepository _collections;

    public GetLogEntriesQueryHandler(ILogCollectionRepository collections) => _collections = collections;

    public async Task<LogEntriesPageDto> Handle(GetLogEntriesQuery request, CancellationToken cancellationToken)
    {
        var collection = await _collections.GetByIdAsync(request.CollectionId, cancellationToken);
        if (collection is null || collection.HostId != request.HostId || collection.Status != LogCollectionStatus.Completed)
        {
            throw new NotFoundException("No collected log for this device.");
        }

        var filtered = Filter(LogPayload.Read(collection.Content), request.Severity, request.Search, request.AroundUtc, request.AroundMinutes)
            .OrderByDescending(e => e.T)
            .ToList();

        var take = Math.Clamp(request.Take, 1, MaxTake);
        return new LogEntriesPageDto(filtered.Count, filtered.Skip(Math.Max(0, request.Skip)).Take(take).ToList());
    }

    public static IEnumerable<LogEntryDto> Filter(IEnumerable<LogEntryDto> entries, string? severity, string? search, DateTimeOffset? around, int aroundMinutes)
    {
        var result = entries;
        result = severity switch
        {
            "errors" => result.Where(e => e.Sev is "Critical" or "Error"),
            "warnings" => result.Where(e => e.Sev == "Warning"),
            _ => result,
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            result = result.Where(e => e.Src.Contains(search, StringComparison.OrdinalIgnoreCase) || e.Msg.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (around is { } at)
        {
            var window = TimeSpan.FromMinutes(Math.Clamp(aroundMinutes, 1, 120));
            result = result.Where(e => (e.T - at).Duration() <= window);
        }

        return result;
    }
}

public record GetLogDownloadQuery(Guid HostId, Guid CollectionId, string Actor) : IRequest<LogDownloadDto>;

public class GetLogDownloadQueryHandler : IRequestHandler<GetLogDownloadQuery, LogDownloadDto>
{
    private readonly IHostRepository _hosts;
    private readonly ILogCollectionRepository _collections;
    private readonly IHostEventRecorder _events;
    private readonly IUnitOfWork _unitOfWork;

    public GetLogDownloadQueryHandler(IHostRepository hosts, ILogCollectionRepository collections, IHostEventRecorder events, IUnitOfWork unitOfWork)
    {
        _hosts = hosts;
        _collections = collections;
        _events = events;
        _unitOfWork = unitOfWork;
    }

    public async Task<LogDownloadDto> Handle(GetLogDownloadQuery request, CancellationToken cancellationToken)
    {
        var host = await _hosts.GetByIdAsync(request.HostId, cancellationToken) ?? throw new NotFoundException("No such device.");
        var collection = await _collections.GetByIdAsync(request.CollectionId, cancellationToken);
        if (collection is null || collection.HostId != host.Id || collection.Status != LogCollectionStatus.Completed)
        {
            throw new NotFoundException("No collected log for this device.");
        }

        var text = new StringBuilder();
        text.AppendLine($"# {host.Hostname} ({host.SerialNumber}), last {collection.PeriodDays} days, {collection.Detail}");
        text.AppendLine($"# Collected {collection.CompletedUtc:u}; downloaded by {request.Actor}");
        if (collection.TruncatedBeforeUtc is { } cut)
        {
            text.AppendLine($"# Cut at the size cap: nothing before {cut:u} is included.");
        }

        foreach (var e in LogPayload.Read(collection.Content))
        {
            text.AppendLine($"{e.T:yyyy-MM-dd HH:mm:ss}Z\t{e.Sev.ToUpperInvariant()}\t{e.Src}\t{e.Msg.ReplaceLineEndings(" ")}");
        }

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("logs.txt", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(text.ToString());
        }

        await _events.LogsDownloadedAsync(host.Id, request.Actor, DateTimeOffset.UtcNow, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var safeName = new string(host.Hostname.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return new LogDownloadDto($"{safeName}-logs.zip", output.ToArray());
    }
}
