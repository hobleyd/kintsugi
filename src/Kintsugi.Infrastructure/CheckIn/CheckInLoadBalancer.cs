using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.CheckIn;

/// <summary>
/// In-memory, per-process implementation of <see cref="ICheckInLoadBalancer"/>: one set of
/// distinct host serial numbers per minute-of-hour, reset whenever the wall-clock hour rolls over
/// so "more load" always means relative to check-ins seen so far in the *current* hour — an
/// ever-growing total would eventually flag every minute as overloaded relative to whichever one
/// happened to start slow. Registered as a singleton (see DependencyInjection) so its state
/// survives across requests within this process's lifetime.
/// </summary>
public class CheckInLoadBalancer : ICheckInLoadBalancer
{
    private const int MinutesPerHour = 60;

    /// <summary>
    /// Only reassign once a minute has meaningfully more distinct hosts on it than the
    /// least-loaded one — a gap of one or two is normal noise, not worth an agent rewriting its
    /// own LaunchDaemon plist over.
    /// </summary>
    private const int ReassignThreshold = 3;

    private readonly object _gate = new();
    private readonly HashSet<string>[] _hostsByMinute = Enumerable.Range(0, MinutesPerHour).Select(_ => new HashSet<string>()).ToArray();
    private readonly Random _random = new();
    private long _windowKey;

    public int? RecordCheckIn(string serialNumber, int minute)
    {
        if (minute < 0 || minute >= MinutesPerHour)
        {
            throw new ArgumentOutOfRangeException(nameof(minute), minute, $"Minute must be between 0 and {MinutesPerHour - 1}.");
        }

        lock (_gate)
        {
            ResetIfNewHour();
            _hostsByMinute[minute].Add(serialNumber);

            var min = _hostsByMinute.Min(hosts => hosts.Count);
            return _hostsByMinute[minute].Count - min < ReassignThreshold ? null : LeastLoadedMinute(min);
        }
    }

    private void ResetIfNewHour()
    {
        var currentWindow = DateTimeOffset.UtcNow.Ticks / TimeSpan.FromHours(1).Ticks;
        if (currentWindow == _windowKey)
        {
            return;
        }

        foreach (var hosts in _hostsByMinute)
        {
            hosts.Clear();
        }

        _windowKey = currentWindow;
    }

    /// <summary>Picks uniformly among every minute currently tied for least-loaded, rather than
    /// always the lowest index — so a wave of hosts reassigned at once doesn't all funnel onto the
    /// same new minute and just recreate the hotspot one slot over.</summary>
    private int LeastLoadedMinute(int min)
    {
        var candidates = new List<int>();
        for (var i = 0; i < MinutesPerHour; i++)
        {
            if (_hostsByMinute[i].Count == min)
            {
                candidates.Add(i);
            }
        }

        return candidates[_random.Next(candidates.Count)];
    }
}
