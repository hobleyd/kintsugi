using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

public class InstalledApplication : BaseEntity
{
    public Guid HostId { get; private set; }
    public string Name { get; private set; } = default!;
    public string Version { get; private set; } = default!;

    /// <summary>
    /// The app bundle's CFBundleIdentifier (e.g. "com.example.MyApp"). Null for
    /// applications not sourced from a scanned app bundle, e.g. Homebrew entries.
    /// </summary>
    public string? ApplicationIdentifier { get; private set; }

    /// <summary>
    /// The InstalledApplication (on the same host) that manages this one, e.g. a
    /// Homebrew formula's parent is the "Homebrew" row for that host. Null for
    /// applications not installed via a tracked package manager, and for the
    /// package manager's own row.
    /// </summary>
    public Guid? ParentApplicationId { get; private set; }

    private InstalledApplication()
    {
    }

    public InstalledApplication(Guid hostId, string name, string version, string? applicationIdentifier = null)
    {
        if (hostId == Guid.Empty)
        {
            throw new DomainException("HostId is required.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Application name is required.");
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new DomainException("Application version is required.");
        }

        HostId = hostId;
        Name = name;
        Version = version;
        ApplicationIdentifier = applicationIdentifier;
    }

    public void SetParent(Guid parentApplicationId)
    {
        if (parentApplicationId == Guid.Empty)
        {
            throw new DomainException("ParentApplicationId cannot be empty.");
        }

        ParentApplicationId = parentApplicationId;
    }

    /// <summary>
    /// Records a newly patched version for this installation, e.g. right after an agent applies
    /// an upgrade — so the server's record reflects it immediately rather than waiting on that
    /// host's next full inventory report.
    /// </summary>
    public void UpdateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new DomainException("Application version is required.");
        }

        Version = version;
        MarkUpdated();
    }
}
