using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// One administrator's request for one device's agent to read the operating system's own log and send
/// it back, and — once it has — the log itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server never pushes.</b> Like <see cref="ForcedPatchRun"/>, this is a row an agent finds when it
/// next checks in (hourly), so the page says "waiting for the device to check in" rather than implying
/// it is instant. A request nobody picked up within <see cref="RequestLifetime"/> is a failed one, and a
/// pick-up that never answered within <see cref="CollectingTimeout"/> is too: both are worked out when
/// read (<see cref="EffectiveStatusAt"/>), so nothing has to run to expire them.
/// </para>
/// <para>
/// <b>Kept for 30 days, then deleted</b> (<see cref="Retention"/>), by the same daily job that prunes the
/// host timeline. <see cref="Content"/> is a gzip of sanitised JSON entries, never the raw upload, and is
/// only ever read by the page that shows it.
/// </para>
/// </remarks>
public class LogCollection : BaseEntity
{
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan CollectingTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public const int MaxFailureReasonLength = 500;
    public const int MaxRequestedByLength = 255;

    public static readonly int[] AllowedPeriodDays = [7, 30];

    public Guid HostId { get; private set; }

    public string RequestedBy { get; private set; } = default!;

    public DateTimeOffset RequestedUtc { get; private set; }

    public DateTimeOffset ExpiresUtc { get; private set; }

    public int PeriodDays { get; private set; }

    public LogCollectionDetail Detail { get; private set; }

    public LogCollectionStatus Status { get; private set; } = LogCollectionStatus.Requested;

    public DateTimeOffset? ClaimedUtc { get; private set; }

    public DateTimeOffset? CompletedUtc { get; private set; }

    public string? FailureReason { get; private set; }

    public int EntryCount { get; private set; }

    /// <summary>Size of <see cref="Content"/>, gzip.</summary>
    public long SizeBytes { get; private set; }

    /// <summary>When the agent had to stop at its size cap: nothing older than this is included.</summary>
    public DateTimeOffset? TruncatedBeforeUtc { get; private set; }

    /// <summary>Gzip of the sanitised JSON entries. Null until completed.</summary>
    public byte[]? Content { get; private set; }

    private LogCollection()
    {
    }

    public static LogCollection Request(Guid hostId, string requestedBy, int periodDays, LogCollectionDetail detail, DateTimeOffset now)
    {
        if (hostId == Guid.Empty)
        {
            throw new DomainException("A log collection must name the device it is for.");
        }

        if (string.IsNullOrWhiteSpace(requestedBy))
        {
            throw new DomainException("A log collection must say who asked for it.");
        }

        if (!AllowedPeriodDays.Contains(periodDays))
        {
            throw new DomainException($"A log collection covers {string.Join(" or ", AllowedPeriodDays)} days.");
        }

        return new LogCollection
        {
            HostId = hostId,
            RequestedBy = requestedBy.Length > MaxRequestedByLength ? requestedBy[..MaxRequestedByLength] : requestedBy,
            RequestedUtc = now,
            ExpiresUtc = now + RequestLifetime,
            PeriodDays = periodDays,
            Detail = detail,
        };
    }

    /// <summary>The status as it is at <paramref name="now"/>: a request nobody picked up in time, or a
    /// pick-up that never answered, is failed whatever the stored value says.</summary>
    public LogCollectionStatus EffectiveStatusAt(DateTimeOffset now) => EffectiveStatus(Status, ExpiresUtc, ClaimedUtc, now);

    public static LogCollectionStatus EffectiveStatus(LogCollectionStatus status, DateTimeOffset expiresUtc, DateTimeOffset? claimedUtc, DateTimeOffset now) =>
        status switch
        {
            LogCollectionStatus.Requested when now >= expiresUtc => LogCollectionStatus.Failed,
            LogCollectionStatus.Collecting when claimedUtc is { } c && now >= c + CollectingTimeout => LogCollectionStatus.Failed,
            _ => status,
        };

    public static string? EffectiveFailureReason(LogCollectionStatus stored, string? reason, DateTimeOffset expiresUtc, DateTimeOffset? claimedUtc, DateTimeOffset now) =>
        EffectiveStatus(stored, expiresUtc, claimedUtc, now) != LogCollectionStatus.Failed ? null
        : reason ?? (stored == LogCollectionStatus.Requested
            ? "The device did not check in within 24 hours, so the request was dropped."
            : "The device picked the request up but never sent the log back.");

    /// <summary>Still waiting for, or being read by, an agent.</summary>
    public bool IsActiveAt(DateTimeOffset now) => EffectiveStatusAt(now) is LogCollectionStatus.Requested or LogCollectionStatus.Collecting;

    public bool IsCollectableAt(DateTimeOffset now) => Status == LogCollectionStatus.Requested && now < ExpiresUtc;

    public void MarkClaimed(DateTimeOffset now)
    {
        if (!IsCollectableAt(now))
        {
            throw new DomainException("This log collection can no longer be picked up.");
        }

        Status = LogCollectionStatus.Collecting;
        ClaimedUtc = now;
        MarkUpdated();
    }

    /// <summary>Throws unless an agent has picked this collection up. Called before an upload is unzipped and parsed, so a
    /// repeated upload against a finished collection costs nothing.</summary>
    public void EnsureCanComplete()
    {
        if (Status != LogCollectionStatus.Collecting)
        {
            throw new DomainException("Only a collection an agent has picked up can be completed.");
        }
    }

    public void Complete(DateTimeOffset now, byte[] content, int entryCount, DateTimeOffset? truncatedBeforeUtc)
    {
        EnsureCanComplete();

        Status = LogCollectionStatus.Completed;
        CompletedUtc = now;
        Content = content;
        SizeBytes = content.Length;
        EntryCount = entryCount;
        TruncatedBeforeUtc = truncatedBeforeUtc;
        FailureReason = null;
        MarkUpdated();
    }

    public void Fail(DateTimeOffset now, string reason)
    {
        if (Status != LogCollectionStatus.Collecting)
        {
            throw new DomainException("Only a collection an agent has picked up can be failed by the agent.");
        }

        Status = LogCollectionStatus.Failed;
        CompletedUtc = now;
        FailureReason = reason.Length > MaxFailureReasonLength ? reason[..MaxFailureReasonLength] : reason;
        MarkUpdated();
    }

    /// <summary>When the row, and the log in it, is deleted.</summary>
    public DateTimeOffset KeepUntilUtc => (CompletedUtc ?? RequestedUtc) + Retention;
}
