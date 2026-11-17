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

    /// <summary>How many rows <see cref="GetResults"/> keeps; the same bound, and the same reason, as
    /// <c>UpdateCheckCoordinator</c>'s.</summary>
    private const int MaxRows = 5000;

    private readonly List<RunRowDto> _rows = new();
    private int _unlistedRows;

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
            _rows.Clear();
            _unlistedRows = 0;
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

            RunRowOutcome outcome;
            if (result.Skipped)
            {
                _skipped++;
                outcome = RunRowOutcome.AlreadyKnown;
            }
            else
            {
                switch (result.Status)
                {
                    case UpgradePathStatus.Found: _resolved++; break;
                    case UpgradePathStatus.NotFound: _notFound++; break;
                    case UpgradePathStatus.Failed: _failed++; break;
                }

                outcome = result.Status switch
                {
                    UpgradePathStatus.Found => RunRowOutcome.Resolved,
                    UpgradePathStatus.NotFound => RunRowOutcome.NoKnownPath,
                    _ => RunRowOutcome.Failed
                };
            }

            // Every row, not only the ones with a note — a resolved row usually has none, so the
            // notes can never say which rows were fine, and the Diagnostics page's tabs are that split.
            if (_rows.Count < MaxRows)
            {
                _rows.Add(new RunRowDto(result.ApplicationName, result.Platform, outcome, result.Note));
            }
            else
            {
                _unlistedRows++;
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

    public UpgradePathRunResultsDto GetResults()
    {
        lock (_lock)
        {
            return new UpgradePathRunResultsDto(_rows.ToList(), _unlistedRows);
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
