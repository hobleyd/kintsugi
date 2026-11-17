using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.LogCollections;

/// <summary>What the page shows of one collection. <c>Status</c> is the effective one: "Requested", "Collecting",
/// "Completed" or "Failed" (a request nobody picked up in 24 hours is Failed, with the reason).</summary>
public record LogCollectionDto(
    Guid Id,
    string Status,
    DateTimeOffset RequestedUtc,
    string RequestedBy,
    int PeriodDays,
    string Detail,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset? ClaimedUtc,
    DateTimeOffset? CompletedUtc,
    string? FailureReason,
    int EntryCount,
    long SizeBytes,
    DateTimeOffset? TruncatedBeforeUtc,
    DateTimeOffset KeepUntilUtc);

/// <summary>A collection without its content, as the repository reads it for the page.</summary>
public record LogCollectionSummary(
    Guid Id,
    Guid HostId,
    LogCollectionStatus Status,
    DateTimeOffset RequestedUtc,
    string RequestedBy,
    int PeriodDays,
    LogCollectionDetail Detail,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset? ClaimedUtc,
    DateTimeOffset? CompletedUtc,
    string? FailureReason,
    int EntryCount,
    long SizeBytes,
    DateTimeOffset? TruncatedBeforeUtc)
{
    public LogCollectionDto ToDto(DateTimeOffset now)
    {
        var status = LogCollection.EffectiveStatus(Status, ExpiresUtc, ClaimedUtc, now);
        return new LogCollectionDto(
            Id, status.ToString(), RequestedUtc, RequestedBy, PeriodDays, Detail.ToString(), ExpiresUtc, ClaimedUtc, CompletedUtc,
            LogCollection.EffectiveFailureReason(Status, FailureReason, ExpiresUtc, ClaimedUtc, now),
            EntryCount, SizeBytes, TruncatedBeforeUtc, (CompletedUtc ?? RequestedUtc) + LogCollection.Retention);
    }

    public static LogCollectionSummary From(LogCollection c) => new(
        c.Id, c.HostId, c.Status, c.RequestedUtc, c.RequestedBy, c.PeriodDays, c.Detail, c.ExpiresUtc, c.ClaimedUtc, c.CompletedUtc,
        c.FailureReason, c.EntryCount, c.SizeBytes, c.TruncatedBeforeUtc);
}

/// <summary>What an agent is handed when it asks: the id to answer with, and what to read.</summary>
public record LogCollectionRequestDto(Guid Id, int PeriodDays, string Detail);

/// <summary>One line of a collected log. <c>Sev</c> is "Critical", "Error", "Warning" or "Info".</summary>
public record LogEntryDto(DateTimeOffset T, string Sev, string Src, string Msg);

public record LogEntriesPageDto(int Total, IReadOnlyList<LogEntryDto> Items);

public record LogDownloadDto(string FileName, byte[] Zip);
