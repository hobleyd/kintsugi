using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Authentication;

/// <summary>
/// One entry of the administrators list as the Administrators page shows it.
/// </summary>
/// <param name="AddedBy">The email of the administrator who added this one; null when that is not
/// known — the entry predates tracking, or was added from the Authentication screen's text box.</param>
/// <param name="AddedUtc">When it was added; null when that is not known.</param>
/// <remarks>Hand-mirrored into <c>web/lib/data/models/settings_mapper.dart</c>; see
/// <c>.claude/rules/hand-mirrored-dtos.md</c>.</remarks>
public record AdministratorDto(string Email, string? AddedBy, DateTimeOffset? AddedUtc)
{
    public static IReadOnlyList<AdministratorDto> FromSettings(AuthenticationSettings? settings) =>
        settings is null
            ? Array.Empty<AdministratorDto>()
            : settings.AdminRecords().Select(r => new AdministratorDto(r.Email, r.AddedBy, r.AddedUtc)).ToList();
}
