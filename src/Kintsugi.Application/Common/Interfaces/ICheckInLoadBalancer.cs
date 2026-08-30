namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Spreads the fleet's check-ins across the hour rather than letting them clump onto whichever
/// minute happens to be busy — see clients/macos-agent/src/checkin_schedule.rs, which is what
/// actually acts on a reassignment. Purely an in-memory, best-effort heuristic: it isn't persisted
/// and isn't shared across API replicas, so losing its counts on a restart just means load
/// tracking starts over, which costs nothing beyond a brief window where a reassignment might be
/// missed.
/// </summary>
public interface ICheckInLoadBalancer
{
    /// <summary>
    /// Records that <paramref name="serialNumber"/> checked in during <paramref name="minute"/>
    /// (0-59, minute of the hour), and returns a different minute for that host to switch to if
    /// this one is carrying meaningfully more load than others right now — or
    /// <see langword="null"/> to keep using the same one.
    ///
    /// Tracked per distinct host, not per request: a host that checks in on the same minute
    /// several times in one hour (retries, repeated self-update restarts, ...) only ever counts
    /// once against that minute — otherwise a single host stuck retrying could make its own minute
    /// look artificially overloaded and get bounced from one minute to the next indefinitely.
    /// </summary>
    int? RecordCheckIn(string serialNumber, int minute);
}
