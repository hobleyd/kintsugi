using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

public class Patch : BaseEntity
{
    public string Name { get; private set; } = default!;
    public string Vendor { get; private set; } = default!;
    public string Version { get; private set; } = default!;
    public PatchSeverity Severity { get; private set; }
    public string? Description { get; private set; }
    public DateTimeOffset ReleasedUtc { get; private set; }

    private Patch()
    {
    }

    public Patch(string name, string vendor, string version, PatchSeverity severity, DateTimeOffset releasedUtc, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Patch name is required.");
        }

        if (string.IsNullOrWhiteSpace(vendor))
        {
            throw new DomainException("Patch vendor is required.");
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new DomainException("Patch version is required.");
        }

        Name = name;
        Vendor = vendor;
        Version = version;
        Severity = severity;
        ReleasedUtc = releasedUtc;
        Description = description;
    }
}
