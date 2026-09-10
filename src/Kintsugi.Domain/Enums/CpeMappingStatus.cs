namespace Kintsugi.Domain.Enums;

/// <summary>
/// How far an inventory subject — an application name, or a host's operating system — has got
/// towards having a CPE that NVD can be asked about.
/// </summary>
/// <remarks>
/// No JSON converter, like <see cref="AuditProvider"/> and <see cref="AiProvider"/>, so this
/// crosses the wire as an ordinal and declaration order is load-bearing on both ends. Append new
/// members; never insert or reorder. The mirror is <c>CpeMappingStatus</c> in
/// <c>web/lib/domain/entities/enums.dart</c>.
/// </remarks>
public enum CpeMappingStatus
{
    /// <summary>Discovered from the inventory and nothing more. Counted on the Vulnerabilities
    /// screen as "not assessed", which is deliberately a first-class number rather than a silent
    /// omission — a screen that lists only what it managed to match reads as full coverage.</summary>
    Unmapped,

    /// <summary>A vendor and product have been proposed — by the AI provider, or by picking one
    /// from NVD's own CPE dictionary — and confirmed to exist in that dictionary, but no human has
    /// accepted it yet. Nothing is assessed in this state.</summary>
    Suggested,

    /// <summary>A human accepted the vendor and product. This is the only state that produces
    /// findings.</summary>
    Confirmed,

    /// <summary>A human decided this subject has no meaningful CPE — an in-house tool, a package
    /// manager's own row, a bundle NVD has never heard of. Distinct from
    /// <see cref="Unmapped"/> because it is a decision rather than a gap, so it is excluded from
    /// the "not assessed" count instead of nagging forever.</summary>
    NotApplicable
}
