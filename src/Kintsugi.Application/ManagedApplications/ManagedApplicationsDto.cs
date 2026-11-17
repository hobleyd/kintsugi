namespace Kintsugi.Application.ManagedApplications;

/// <summary>One application another tool patches, with how many hosts have it.</summary>
/// <remarks>Hand-mirrored into <c>web/lib/data/models/managed_application_mapper.dart</c>; see
/// <c>.claude/rules/hand-mirrored-dtos.md</c>.</remarks>
public record ManagedApplicationDto(string ApplicationName, string Owner, int HostCount);

/// <summary>Everything the Managed Elsewhere page shows: the applications, and the tools that can own
/// one (the second is what the owner pickers offer).</summary>
public record ManagedApplicationsDto(IReadOnlyList<ManagedApplicationDto> Applications, IReadOnlyList<string> Owners);
