using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Vanta;

namespace Kintsugi.WebApi.Vanta;

/// <summary>
/// In-memory, single-run-at-a-time coordinator between whoever wants a Vanta sync — the settings
/// screen's "Sync now", or the interval timer inside <see cref="VantaSyncBackgroundService"/> — and
/// the background service that performs it.
/// </summary>
/// <remarks>
/// The same shape as <c>UpdateCheckCoordinator</c>, with one addition: the timer needs to start a
/// run without signalling itself, which is what <see cref="TryStartScheduledRun"/> is for. Starting
/// through <see cref="TryRequestStart"/> would leave a permit on the semaphore that the very next
/// wait would consume, and the service would sync twice back to back.
/// </remarks>
public class VantaSyncCoordinator : IVantaSyncCoordinator
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _lock = new();

    private bool _running;
    private DateTimeOffset? _startedUtc;
    private DateTimeOffset? _completedUtc;
    private bool? _lastRunSucceeded;
    private int _componentCount;
    private int _packageCount;
    private string? _message;

    /// <inheritdoc />
    public bool TryRequestStart()
    {
        if (!TryStartScheduledRun())
        {
            return false;
        }

        if (_signal.CurrentCount == 0)
        {
            _signal.Release();
        }

        return true;
    }

    /// <summary>Marks a run as started without waking the waiter — for the background service's own
    /// timer, which is already awake and about to run it.</summary>
    public bool TryStartScheduledRun()
    {
        lock (_lock)
        {
            if (_running)
            {
                return false;
            }

            _running = true;
            _startedUtc = DateTimeOffset.UtcNow;
            _completedUtc = null;
            return true;
        }
    }

    /// <summary>Records the outcome of a completed run — including one that reached Vanta and was
    /// refused, which <see cref="VantaSyncResultDto.Succeeded"/> reports as a failure with the
    /// reason in its message rather than as an exception.</summary>
    public void Complete(VantaSyncResultDto result)
    {
        lock (_lock)
        {
            _running = false;
            _completedUtc = DateTimeOffset.UtcNow;
            _lastRunSucceeded = result.Succeeded;
            _componentCount = result.ComponentCount;
            _packageCount = result.PackageCount;
            _message = result.Message;
        }
    }

    public void Fault(string reason)
    {
        lock (_lock)
        {
            _running = false;
            _completedUtc = DateTimeOffset.UtcNow;
            _lastRunSucceeded = false;
            _componentCount = 0;
            _packageCount = 0;
            _message = reason;
        }
    }

    /// <summary>When the last run finished, whatever its outcome — what the background service
    /// measures its interval from, so a failed run does not immediately retry in a tight loop.</summary>
    public DateTimeOffset? LastCompletedUtc
    {
        get
        {
            lock (_lock)
            {
                return _completedUtc;
            }
        }
    }

    public Task<bool> WaitForSignalAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);

    public VantaSyncStatusDto GetStatus()
    {
        lock (_lock)
        {
            return new VantaSyncStatusDto(
                _running, _startedUtc, _completedUtc, _lastRunSucceeded, _componentCount, _packageCount, _message);
        }
    }
}
