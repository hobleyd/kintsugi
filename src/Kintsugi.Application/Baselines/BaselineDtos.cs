using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Baselines;

/// <summary>How many hosts are in each state for one item, or across a baseline. <paramref name="NotReported"/>
/// is a host the baseline applies to that has sent nothing for it yet — coverage, which in audit mode is
/// half of what the numbers mean.</summary>
public record ComplianceCountsDto(int Compliant, int Drifted, int NotApplicable, int Error, int NotReported);

/// <summary>A row of the Baselines screen. Read by <c>web/lib/data/models/baseline_mapper.dart</c>.</summary>
/// <param name="SignedVersion">The version agents are given, or null when nothing has been signed yet.</param>
/// <param name="Totals">Summed over the signed version's settings and the hosts it applies to.</param>
public record BaselineSummaryDto(
    Guid Id,
    string Name,
    int LatestVersion,
    int? SignedVersion,
    bool AssignedToAllWindowsHosts,
    int ApplicableHostCount,
    int SettingCount,
    ComplianceCountsDto Totals);

public record BaselineVersionDto(
    Guid Id,
    int Number,
    string Document,
    string DocumentSha256,
    bool Signed,
    string? SignedBy,
    DateTimeOffset? SignedUtc,
    string? CreatedBy,
    DateTimeOffset CreatedUtc);

/// <summary>One item of the signed version with how many hosts are in each state for it. Facts carry
/// zero counts: they are reported, never judged.</summary>
public record BaselineItemComplianceDto(BaselineItem Item, ComplianceCountsDto Counts);

public record BaselineHostComplianceDto(Guid HostId, string Hostname, ComplianceCountsDto Counts, DateTimeOffset? LastCheckedUtc);

/// <param name="Versions">Newest first. The signed version agents run is the highest-numbered signed one.</param>
/// <param name="Items">The signed version's items, empty until one is signed.</param>
public record BaselineDetailDto(
    Guid Id,
    string Name,
    bool AssignedToAllWindowsHosts,
    IReadOnlyList<string> AssignedHostNames,
    IReadOnlyList<BaselineVersionDto> Versions,
    IReadOnlyList<BaselineItemComplianceDto> Items,
    IReadOnlyList<BaselineHostComplianceDto> Hosts);

public record HostItemStateDto(
    BaselineItem Item,
    BaselineItemState? State,
    string? ObservedValue,
    string? Detail,
    DateTimeOffset? LastCheckedUtc,
    DateTimeOffset? LastChangedUtc);

public record HostFactDto(string Key, string? Value, DateTimeOffset LastCheckedUtc, DateTimeOffset LastChangedUtc);

public record BaselineHostDetailDto(Guid HostId, string Hostname, int? SignedVersion, IReadOnlyList<HostItemStateDto> Items, IReadOnlyList<HostFactDto> Facts);

public record CreatedBaselineVersionDto(Guid BaselineId, Guid VersionId, int Number, string DocumentSha256);

/// <summary>
/// What <c>GET /api/baseline</c> hands an agent. Mirrored by hand by <c>BaselineResponse</c> in
/// <c>clients/windows-agent/src/baseline.rs</c> — Windows only in this step.
/// </summary>
public record AgentBaselinesDto(IReadOnlyList<AgentBaselineDto> Baselines);

/// <summary>One signed baseline for the agent, and what the server already holds for this host against
/// it, so the agent can send only what changed.</summary>
/// <param name="Document">The signed canonical JSON, byte for byte.</param>
/// <param name="Signature">Base64 DER ECDSA-SHA256 over <paramref name="Document"/>'s UTF-8 bytes.</param>
public record AgentBaselineDto(
    Guid VersionId,
    string Document,
    string Signature,
    IReadOnlyList<AgentItemStateDto> States,
    IReadOnlyList<AgentFactDto> Facts);

/// <param name="State">The <see cref="BaselineItemState"/> member name — names rather than ordinals on the
/// agent's wire, so a reordered enum cannot silently relabel a host's report.</param>
public record AgentItemStateDto(string Key, string State, string? Observed);

public record AgentFactDto(string Key, string? Value);
