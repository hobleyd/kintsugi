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
    /// <summary>
    /// How many per-row reasons are listed before the rest are reported as a count.
    /// </summary>
    /// <remarks>
    /// A run targets every script row in the database, where the scan this is modelled on works
    /// through applications and skips everything already resolved — so one broken bucket here can
    /// produce a note per row, and the status is polled every three seconds for as long as the run
    /// lasts. Capped rather than left unbounded for that reason, and the overflow is *stated* (see
    /// <see cref="GetStatus"/>) rather than silently dropped: a list that stops at fifty with
    /// nothing to say so reads as fifty being all there was.
    /// </remarks>
    private const int MaxNotes = 50;

    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _lock = new();

    private bool _running;
    private int _total;
    private int _completed;
    private int _updated;
    private int _unchanged;
    private int _failed;
    private int _skipped;
    private DateTimeOffset? _startedUtc;
    private DateTimeOffset? _completedUtc;
    private string? _faultReason;
    private readonly List<string> _notes = new();
    private int _unlistedNotes;

    public bool TryRequestStart()
    {
        lock (_lock)
        {
            if (_running)
            {
                return false;
            }

            _running = true;
            _total = _completed = _updated = _unchanged = _failed = _skipped = 0;
            _notes.Clear();
            _unlistedNotes = 0;
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

            if (result.Skipped)
            {
                _skipped++;
            }
            else if (!result.Success)
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

            // Same shape as UpgradePathScanCoordinator's: a successful check carries no note, so
            // this collects exactly the rows a reader would otherwise have to go looking for —
            // and would not find, since neither a skip nor a failure writes anything to the row.
            if (!string.IsNullOrWhiteSpace(result.Note))
            {
                if (_notes.Count < MaxNotes)
                {
                    _notes.Add($"{result.ApplicationName} ({result.Platform}): {result.Note}");
                }
                else
                {
                    _unlistedNotes++;
                }
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
            var notes = _notes.ToList();
            if (_unlistedNotes > 0)
            {
                notes.Add($"...and {_unlistedNotes} more not listed. The counts above cover all of them.");
            }

            return new UpdateCheckStatusDto(
                _running, _total, _completed, _updated, _unchanged, _failed, _skipped,
                _startedUtc, _completedUtc, _faultReason, notes);
        }
    }
}
