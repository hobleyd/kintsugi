using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Autonomy;

/// <summary>A successful patch, and whether an administrator started it.</summary>
public record ClassifiedPatch(Guid HostId, string Subject, DateTimeOffset OccurredUtc, bool AdminStarted);

/// <summary>
/// Decides, for each successful patch, whether an administrator started it or the host ran it on its
/// own schedule.
/// </summary>
/// <remarks>
/// <para>
/// A patch result does not say whether the run was scheduled or forced, so this is inferred from the
/// Patch now records, and the rule is exact so that nobody can argue the numbers either way: a patch
/// is <b>admin-started</b> when the same host collected a Patch now request for the same thing (an
/// application, or "Windows") before it, that patch is the next one for that host and subject, and it
/// came within <see cref="Window"/> of the request. Every other patch is on the host's own schedule.
/// </para>
/// <para>
/// <see cref="Window"/> is 24 hours because that is how long a forced run lives
/// (<c>ForcedPatchRun.DefaultLifetime</c>): a request nobody collected by then is gone, so a patch
/// more than a day after it cannot have been caused by it. The next patch consumes the request, so one
/// press is never counted for two patches.
/// </para>
/// <para>
/// What this cannot see is a host deciding for itself (hold, prompt, delay): the agents do not report
/// that, which is why the page says "on the host's own schedule" and never "the host decided".
/// </para>
/// </remarks>
public static class AutonomyPatchClassifier
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public static IReadOnlyList<ClassifiedPatch> Classify(IEnumerable<PatchActivityRecord> records)
    {
        var result = new List<ClassifiedPatch>();

        var groups = records
            .Where(r => r.Kind is HostEventKind.ApplicationPatched or HostEventKind.OperatingSystemPatched or HostEventKind.PatchNowPickedUp)
            .GroupBy(r => (r.HostId, Subject: r.Subject.ToLowerInvariant()));

        foreach (var group in groups)
        {
            DateTimeOffset? pickedUp = null;

            // A pickup sorts before a patch at the same instant, so a patch that finished in the same
            // second the host collected the request still counts as the request's.
            foreach (var record in group.OrderBy(r => r.OccurredUtc).ThenBy(r => r.Kind == HostEventKind.PatchNowPickedUp ? 0 : 1))
            {
                if (record.Kind == HostEventKind.PatchNowPickedUp)
                {
                    pickedUp = record.OccurredUtc;
                    continue;
                }

                var adminStarted = pickedUp is { } at && record.OccurredUtc - at <= Window;
                pickedUp = null;
                result.Add(new ClassifiedPatch(record.HostId, record.Subject, record.OccurredUtc, adminStarted));
            }
        }

        return result;
    }
}
