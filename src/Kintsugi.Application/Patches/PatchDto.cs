using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Patches;

public record PatchDto(Guid Id, string Name, string Vendor, string Version, PatchSeverity Severity, string? Description, DateTimeOffset ReleasedUtc)
{
    public static PatchDto FromEntity(Patch patch) =>
        new(patch.Id, patch.Name, patch.Vendor, patch.Version, patch.Severity, patch.Description, patch.ReleasedUtc);
}
