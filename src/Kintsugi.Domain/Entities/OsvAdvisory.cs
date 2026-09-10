using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// A cache of "which CVEs does this OSV record stand for", so the same advisory is not fetched
/// again on every run.
/// </summary>
/// <remarks>
/// <para>
/// OSV's batch query answers with its own identifiers and nothing else. Some carry the CVE in the
/// name — <c>UBUNTU-CVE-2024-2511</c>, <c>DEBIAN-CVE-2023-5678</c>, <c>ALPINE-CVE-2024-4603</c> —
/// and those need no lookup. The RHEL family's do not: <c>RLSA-2022:7288</c> is one advisory
/// standing for <c>CVE-2022-3602</c> and <c>CVE-2022-3786</c>, and the only way to learn that is
/// <c>GET /v1/vulns/{id}</c>.
/// </para>
/// <para>
/// Cached because the answer never changes and the alternative is hundreds of round trips per run
/// forever. <see cref="CveIds"/> is a space-separated list rather than a child table on purpose:
/// this is a memo of somebody else's fact, read as a unit and never queried into, and the
/// <see cref="Vulnerability"/> rows it resolves to are the relational half.
/// </para>
/// </remarks>
public class OsvAdvisory : BaseEntity
{
    /// <summary>OSV's own identifier, e.g. <c>RLSA-2022:7288</c>. Unique.</summary>
    public string OsvId { get; private set; } = default!;

    /// <summary>The CVE identifiers this advisory stands for, space-separated. Empty when OSV
    /// knows of none — a distribution-only advisory with no CVE assigned, which is a real and
    /// uninteresting case rather than a failure, and is cached so it is not re-fetched.</summary>
    public string CveIds { get; private set; } = string.Empty;

    private OsvAdvisory()
    {
    }

    public static OsvAdvisory Create(string osvId, IEnumerable<string> cveIds)
    {
        if (string.IsNullOrWhiteSpace(osvId))
        {
            throw new DomainException("An OSV advisory needs an identifier.");
        }

        return new OsvAdvisory
        {
            OsvId = osvId.Trim(),
            CveIds = string.Join(' ', Normalize(cveIds))
        };
    }

    public void Update(IEnumerable<string> cveIds)
    {
        CveIds = string.Join(' ', Normalize(cveIds));
        MarkUpdated();
    }

    public IReadOnlyList<string> Cves =>
        CveIds.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Reads the CVE this identifier names, when it names one.
    /// </summary>
    /// <remarks>
    /// Every distribution OSV covers except the RHEL family embeds the CVE in its own identifier,
    /// so this resolves the overwhelming majority without a network call at all. Anchored on the
    /// suffix rather than on a prefix list, so a distribution nobody has thought of yet is handled
    /// the moment it follows the same convention.
    /// </remarks>
    public static string? CveFromIdentifier(string osvId)
    {
        var index = osvId.IndexOf("CVE-", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var candidate = osvId[index..].Trim().ToUpperInvariant();

        // CVE-YYYY-NNNN..., and nothing may follow it — "UBUNTU-CVE-2024-2511" qualifies while a
        // hypothetical "CVE-2024-2511-PATCH-2" does not, because that is not the CVE it names.
        var parts = candidate.Split('-');
        if (parts.Length != 3 || parts[1].Length != 4 || !parts[1].All(char.IsAsciiDigit) || !parts[2].All(char.IsAsciiDigit))
        {
            return null;
        }

        return candidate;
    }

    private static IEnumerable<string> Normalize(IEnumerable<string> cveIds) =>
        cveIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.Ordinal);
}
