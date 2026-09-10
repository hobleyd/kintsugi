using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// The record that NVD was asked about one (<see cref="CpeMapping"/>, version) pair, when, and what
/// came back. The findings themselves hang off it as <see cref="VulnerabilityMatch"/> rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyed on the product and version, not on a host.</b> A CVE affects Firefox 130.0 wherever it
/// is installed, so the assessment is stored once and the hosts running that version are joined at
/// query time — which is also what keeps it correct across
/// <c>RegisterApplicationsCommandHandler</c>'s hourly delete-and-recreate of every installed
/// application row. It is the same shape <c>upgrade_paths</c> already uses: one row per
/// (application, platform), never per host.
/// </para>
/// <para>
/// <b><see cref="LastAssessedUtc"/> is a work cursor, not a display field.</b> A full pass is one
/// NVD query per pair — hundreds for a real fleet, against a limit of 5 requests per 30 seconds
/// without an API key. So the background service takes the least recently assessed pairs first,
/// commits each as it goes, and stops at
/// <see cref="VulnerabilitySettings.AssessmentsPerRun"/>. A run that began at the start every time
/// would never reach the tail of the queue, and a container restart would discard everything it
/// had already paid for.
/// </para>
/// </remarks>
public class CpeAssessment : BaseEntity
{
    public Guid CpeMappingId { get; private set; }

    /// <summary>The installed version this assessment asked about, exactly as the agent reported
    /// it. NVD evaluates its own configuration ranges against this, which is why nothing here
    /// interprets or normalizes it.</summary>
    public string Version { get; private set; } = default!;

    /// <summary>When NVD last answered for this pair. Null on a row that has been queued by
    /// discovery and not yet reached — which is what the background service takes first.</summary>
    public DateTimeOffset? LastAssessedUtc { get; private set; }

    /// <summary>How many CVEs matched at the last assessment. Denormalized from the match rows so
    /// the fleet summary does not have to count tens of thousands of them per page load.</summary>
    public int MatchCount { get; private set; }

    /// <summary>How many of <see cref="MatchCount"/> are in CISA's exploited catalog. The number
    /// the Vulnerabilities screen leads with.</summary>
    public int KnownExploitedCount { get; private set; }

    /// <summary>Why the last attempt failed, when it did. Kept so a pair that NVD rejects — an
    /// unescapable version, a product withdrawn from the dictionary — surfaces on the screen
    /// instead of sitting at the head of the queue being retried in silence.</summary>
    public string? LastError { get; private set; }

    private CpeAssessment()
    {
    }

    public static CpeAssessment Queue(Guid cpeMappingId, string version)
    {
        if (cpeMappingId == Guid.Empty)
        {
            throw new DomainException("An assessment needs a CPE mapping.");
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new DomainException("An assessment needs a version.");
        }

        return new CpeAssessment { CpeMappingId = cpeMappingId, Version = version.Trim() };
    }

    /// <summary>Records a successful answer from NVD. The counts are passed in rather than derived
    /// from a navigation property so the caller can write them in the same unit of work that
    /// replaces the match rows.</summary>
    public void RecordAssessment(int matchCount, int knownExploitedCount)
    {
        MatchCount = matchCount;
        KnownExploitedCount = knownExploitedCount;
        LastAssessedUtc = DateTimeOffset.UtcNow;
        LastError = null;
        MarkUpdated();
    }

    /// <summary>
    /// Records that the attempt failed, leaving any previous counts standing.
    /// </summary>
    /// <remarks>
    /// <see cref="LastAssessedUtc"/> is stamped even so, deliberately: it is the queue's ordering
    /// key, and leaving it null on a pair that fails every time would park that pair permanently at
    /// the head of the queue and starve everything behind it. The failure stays visible in
    /// <see cref="LastError"/> instead.
    /// </remarks>
    public void RecordFailure(string error)
    {
        LastError = string.IsNullOrWhiteSpace(error) ? "The assessment failed." : error.Trim();
        LastAssessedUtc = DateTimeOffset.UtcNow;
        MarkUpdated();
    }
}
