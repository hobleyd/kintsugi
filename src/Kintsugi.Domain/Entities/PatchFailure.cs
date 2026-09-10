using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// One host's report that an upgrade script (or package-manager command) failed while it was
/// actually running — the counterpart to <c>ReportPatchResultCommand</c>, which reports the
/// success. Where a success is a fact the server folds into its record of what's installed and
/// then forgets, a failure is a piece of work: somebody has to read the output, decide whether the
/// script is wrong, and fix it. So this is an entity rather than a log line, and the Failed Updates
/// screen is the queue it forms.
/// </summary>
/// <remarks>
/// <para>
/// **One row per (host, application), not one per attempt.** A patch cycle runs on a schedule, so a
/// script that is broken fails again every cycle, forever. Recording each attempt separately would
/// bury the twelve distinct problems an administrator can act on under two thousand rows saying the
/// same thing. <see cref="RecordAnotherFailure"/> folds a repeat into the existing row instead,
/// keeping the first and latest timestamps and a count — which is also the more useful reading, since
/// "failing since Tuesday, 40 times" is what says whether this is a blip or a broken script.
/// </para>
/// <para>
/// **<see cref="Platform"/> is resolved by the server, not sent by the agent.** It is an
/// <c>UpgradePath</c> platform *bucket* ("Homebrew", "winget", "macOS", ...), not an operating
/// system, and it is half the key the script this row is about is stored under. An agent deriving
/// it from its own OS would send "macOS" for a Homebrew row, and the fix panel would then load the
/// wrong script or none at all. <c>ReportPatchFailureCommandHandler</c> runs the same
/// (host, application) → row resolution that served that agent its work list in the first place.
/// Null when nothing resolves — a race against the row being deleted, say — which the screen shows
/// as a failure it cannot offer a fix for rather than hiding.
/// </para>
/// </remarks>
public class PatchFailure : BaseEntity
{
    public Guid HostId { get; private set; }

    public string ApplicationName { get; private set; } = default!;

    /// <summary>The <c>UpgradePath</c> platform bucket this application's script is stored under —
    /// see the remarks on this class for why the server resolves it rather than the agent sending
    /// it, and why it is nullable.</summary>
    public string? Platform { get; private set; }

    /// <summary>The version that was installed when the attempt was made.</summary>
    public string? InstalledVersion { get; private set; }

    /// <summary>The version the attempt was trying to reach — the upgrade path's
    /// <c>LatestVersion</c> as the agent saw it.</summary>
    public string? AttemptedVersion { get; private set; }

    /// <summary>What went wrong: the failing command's exit status and its captured output, as the
    /// agent reported it. Truncated agent-side well below the length the validator accepts — see
    /// <c>ReportPatchFailureCommandValidator</c>, which names the ceiling, and each agent's
    /// <c>upgrade::MAX_REPORTED_FAILURE_BYTES</c>.</summary>
    public string Details { get; private set; } = default!;

    /// <summary>When the first failure in this run of failures happened, on the host's own clock.</summary>
    public DateTimeOffset FirstFailedUtc { get; private set; }

    /// <summary>When the most recent failure happened, on the host's own clock. This is the date the
    /// Failed Updates screen shows, since it is what says whether the problem is still live.</summary>
    public DateTimeOffset LastFailedUtc { get; private set; }

    /// <summary>How many attempts have failed since this row was opened.</summary>
    public int FailureCount { get; private set; }

    public PatchFailureResolution Resolution { get; private set; }

    public DateTimeOffset? ResolvedUtc { get; private set; }

    private PatchFailure()
    {
    }

    public static PatchFailure Open(
        Guid hostId,
        string applicationName,
        string? platform,
        string? installedVersion,
        string? attemptedVersion,
        string details,
        DateTimeOffset failedUtc)
    {
        if (hostId == Guid.Empty)
        {
            throw new DomainException("A patch failure must name the host it happened on.");
        }

        if (string.IsNullOrWhiteSpace(applicationName))
        {
            throw new DomainException("A patch failure must name the application that failed to patch.");
        }

        if (string.IsNullOrWhiteSpace(details))
        {
            throw new DomainException("A patch failure must carry the failure's details — that is the whole point of reporting it.");
        }

        return new PatchFailure
        {
            HostId = hostId,
            ApplicationName = applicationName,
            Platform = string.IsNullOrWhiteSpace(platform) ? null : platform,
            InstalledVersion = string.IsNullOrWhiteSpace(installedVersion) ? null : installedVersion,
            AttemptedVersion = string.IsNullOrWhiteSpace(attemptedVersion) ? null : attemptedVersion,
            Details = details,
            FirstFailedUtc = failedUtc,
            LastFailedUtc = failedUtc,
            FailureCount = 1,
            Resolution = PatchFailureResolution.Outstanding
        };
    }

    /// <summary>
    /// Folds a repeat of the same failure into this row — see the remarks on this class for why
    /// repeats are not separate rows.
    /// </summary>
    /// <remarks>
    /// The *latest* details win rather than the first. A script being worked on fails differently
    /// as it is fixed, and the message that matters is the one describing what it does now; the
    /// earlier one describes a script that no longer exists.
    /// </remarks>
    public void RecordAnotherFailure(
        string? platform,
        string? installedVersion,
        string? attemptedVersion,
        string details,
        DateTimeOffset failedUtc)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            throw new DomainException("A patch failure must carry the failure's details — that is the whole point of reporting it.");
        }

        if (!string.IsNullOrWhiteSpace(platform))
        {
            Platform = platform;
        }

        InstalledVersion = string.IsNullOrWhiteSpace(installedVersion) ? InstalledVersion : installedVersion;
        AttemptedVersion = string.IsNullOrWhiteSpace(attemptedVersion) ? AttemptedVersion : attemptedVersion;
        Details = details;
        // Clock skew, or a host whose report arrived out of order, must not wind this backwards —
        // the column is what the screen sorts on.
        LastFailedUtc = failedUtc > LastFailedUtc ? failedUtc : LastFailedUtc;
        FailureCount++;
        MarkUpdated();
    }

    /// <summary>
    /// Closes this failure. Called with <see cref="PatchFailureResolution.PatchSucceeded"/> when the
    /// same host later reports patching this application, and with
    /// <see cref="PatchFailureResolution.Dismissed"/> when an administrator clears it by hand.
    /// </summary>
    public void Resolve(PatchFailureResolution resolution)
    {
        if (resolution == PatchFailureResolution.Outstanding)
        {
            throw new DomainException("Resolving a patch failure needs a resolution other than 'outstanding'.");
        }

        // Idempotent on purpose: a success report arrives on every cycle after the fix lands, and
        // re-stamping ResolvedUtc each time would keep moving the date a row was actually settled.
        if (Resolution != PatchFailureResolution.Outstanding)
        {
            return;
        }

        Resolution = resolution;
        ResolvedUtc = DateTimeOffset.UtcNow;
        MarkUpdated();
    }

    /// <summary>
    /// Puts a previously-settled row back on the queue, keeping its history. A failure that recurs
    /// after being dismissed — or after a success that turned out not to stick — is the same
    /// problem returning, and opening a second row for it would split the count and the "failing
    /// since" date across two rows that mean one thing.
    /// </summary>
    public void Reopen(
        string? platform,
        string? installedVersion,
        string? attemptedVersion,
        string details,
        DateTimeOffset failedUtc)
    {
        Resolution = PatchFailureResolution.Outstanding;
        ResolvedUtc = null;
        RecordAnotherFailure(platform, installedVersion, attemptedVersion, details, failedUtc);
    }
}
