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

    /// <summary>
    /// The reporting agent's package manager's own verdict on whether this installation has an
    /// update pending: true when <c>flatpak remote-ls --updates</c> / <c>snap refresh --list</c>
    /// named it, false when that listing ran and did not, null when the agent had no verdict to
    /// offer (the listing failed, or the agent — macOS and Windows today — does not report one).
    /// </summary>
    /// <remarks>
    /// Kept apart from the version strings because it is the reliable half. Flatpak knows an
    /// update is pending from the remote's commit alone and prints a version only when the host's
    /// appstream cache holds one; both Flatpak and Snap ship rebuilds under an unchanged version.
    /// A version comparison calls every one of those "current", which is how a Linux host with
    /// pending updates showed 0 app updates. <c>UpgradePathRepository.ComputeUpdateAvailable</c>
    /// takes this verdict whenever it is present and falls back to comparing versions otherwise.
    /// </remarks>
    public bool? UpdateAvailable { get; private set; }

    private InstalledApplication()
    {
    }

    public InstalledApplication(Guid hostId, string name, string version, string? applicationIdentifier = null, bool? updateAvailable = null)
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
        UpdateAvailable = updateAvailable;
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
    /// host's next full inventory report. The manager's verdict is cleared with it: the update it
    /// was reporting has just been applied, and leaving it standing would keep the host counted as
    /// behind until its next inventory report even though the version now says otherwise.
    /// </summary>
    public void UpdateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new DomainException("Application version is required.");
        }

        Version = version;
        UpdateAvailable = false;
        MarkUpdated();
    }
}
