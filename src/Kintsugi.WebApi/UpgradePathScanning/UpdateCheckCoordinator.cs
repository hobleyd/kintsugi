using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Commands.CheckApplicationUpdate;

namespace Kintsugi.WebApi.UpgradePathScanning;

/// <summary>
/// In-memory, single-run-at-a-time coordinator between the "Check for Updates" request and the
/// background service that actually runs it. State lives only for the app's lifetime — a restart
/// mid-run loses progress, but not any already-persisted <c>UpgradePath</c> rows.
/// </summary>
public class UpdateCheckCoordinator : IUpdateCheckCoordinator
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _lock = new();

    private bool _running;
    private int _total;
    private int _completed;
    private int _updated;
    private int _unchanged;
    private int _failed;
    private DateTimeOffset? _startedUtc;
    private DateTimeOffset? _completedUtc;
    private string? _faultReason;

    public bool TryRequestStart()
    {
        lock (_lock)
        {
            if (_running)
            {
                return false;
            }

            _running = true;
            _total = _completed = _updated = _unchanged = _failed = 0;
            _faultReason = null;
            _startedUtc = DateTimeOffset.UtcNow;
            _completedUtc = null;
        }

        if (_signal.CurrentCount == 0)
        {
            _signal.Release();
        }

        return true;
    }

    public Task WaitForSignalAsync(CancellationToken cancellationToken) => _signal.WaitAsync(cancellationToken);

    public void SetTotal(int total)
    {
        lock (_lock)
        {
            _total = total;
        }
    }

    public void ReportItem(CheckApplicationUpdateResult result)
    {
        lock (_lock)
        {
            _completed++;

            if (!result.Success)
            {
                _failed++;
            }
            else if (result.VersionChanged)
            {
                _updated++;
            }
            else
            {
                _unchanged++;
            }
        }
    }

    public void Fault(string reason)
    {
        lock (_lock)
        {
            _faultReason = reason;
            _running = false;
            _completedUtc = DateTimeOffset.UtcNow;
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            _running = false;
            _completedUtc = DateTimeOffset.UtcNow;
        }
    }

    public UpdateCheckStatusDto GetStatus()
    {
        lock (_lock)
        {
            return new UpdateCheckStatusDto(
                _running, _total, _completed, _updated, _unchanged, _failed,
                _startedUtc, _completedUtc, _faultReason);
        }
    }
}
