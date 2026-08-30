using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Commands.ResearchApplicationUpgradePath;
using Kintsugi.Domain.Enums;

namespace Kintsugi.WebApi.UpgradePathScanning;

/// <summary>
/// In-memory, single-scan-at-a-time coordinator between the "Find Upgrade Paths" request and the
/// background service that actually runs a scan. State lives only for the app's lifetime — a
/// restart mid-scan loses progress, but not any already-persisted <c>UpgradePath</c> rows, and the
/// next scan simply skips what's already <see cref="UpgradePathStatus.Found"/> and re-does the rest.
/// </summary>
public class UpgradePathScanCoordinator : IUpgradePathScanCoordinator
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _lock = new();

    private bool _running;
    private int _total;
    private int _completed;
    private int _resolved;
    private int _notFound;
    private int _failed;
    private int _skipped;
    private DateTimeOffset? _startedUtc;
    private DateTimeOffset? _completedUtc;
    private string? _faultReason;
    private readonly List<string> _notes = new();

    public bool TryRequestStart()
    {
        lock (_lock)
        {
            if (_running)
            {
                return false;
            }

            _running = true;
            _total = _completed = _resolved = _notFound = _failed = _skipped = 0;
            _notes.Clear();
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

    public void ReportItem(ResearchApplicationUpgradePathResult result)
    {
        lock (_lock)
        {
            _completed++;

            if (result.Skipped)
            {
                _skipped++;
            }
            else
            {
                switch (result.Status)
                {
                    case UpgradePathStatus.Found: _resolved++; break;
                    case UpgradePathStatus.NotFound: _notFound++; break;
                    case UpgradePathStatus.Failed: _failed++; break;
                }
            }

            if (!string.IsNullOrWhiteSpace(result.Note))
            {
                _notes.Add($"{result.ApplicationName} ({result.Platform}): {result.Note}");
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

    public UpgradePathScanStatusDto GetStatus()
    {
        lock (_lock)
        {
            return new UpgradePathScanStatusDto(
                _running, _total, _completed, _resolved, _notFound, _failed, _skipped,
                _startedUtc, _completedUtc, _faultReason, _notes.ToList());
        }
    }
}
