using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Applications.Commands.RegisterApplications;

/// <summary>
/// Registers the full set of applications installed on a host, identified by
/// its serial number. Replaces any previously reported list for that host
/// (agents report a full inventory snapshot, not incremental changes).
/// </summary>
public record RegisterApplicationsCommand(string SerialNumber, IReadOnlyList<ApplicationEntry> Applications)
    : IRequest<RegisterApplicationsResult>, IAgentScopedRequest;

/// <summary>
/// One reported application. When <see cref="PackageManager"/> is set, it must
/// match the <see cref="Name"/> of another entry in the same report (that
/// entry's own installation, e.g. "Homebrew") — the two are linked as
/// parent/child. Left null (or unmatched), the entry is reported standalone.
/// <see cref="ApplicationIdentifier"/> is whatever stably names this application
/// on its platform: a macOS app bundle's CFBundleIdentifier (e.g.
/// "com.example.MyApp"), a Windows application's key name under the uninstall
/// registry, a winget/Chocolatey package id, or a Homebrew formula name/cask
/// token. Its presence is also the agent's statement that it *can* patch this
/// installation — the agents' <c>is_patchable</c> requires one for any
/// <c>Script</c> row — so the macOS agent deliberately leaves it null for a
/// cask whose upgrade needs root (a <c>pkg</c> installer, a <c>pkgutil</c>
/// uninstall), which its non-root Homebrew run cannot perform; see
/// <c>system_info::cask_requires_root</c>.
/// <see cref="AvailableVersion"/> is the latest version known to be available
/// independently of any upgrade research (a package manager's own catalog
/// version) — when present alongside a <see cref="PackageManager"/> this system
/// recognizes (see <c>PackageManagerCatalog</c>), it seeds that application's
/// <see cref="Kintsugi.Domain.Entities.UpgradePath"/> directly, without waiting
/// on AI research.
/// <see cref="UpdateAvailable"/> is that manager's own verdict on whether an
/// update is pending for this installation, carried separately because the
/// verdict is reliable where the version is optional: Flatpak often has no
/// version to print for a pending update, and Flatpak and Snap both ship
/// rebuilds under an unchanged version string. Null when the agent had no
/// verdict (the listing failed, or the agent does not report one) — see
/// <see cref="Kintsugi.Domain.Entities.InstalledApplication.UpdateAvailable"/>.
/// The Rust mirror is <c>InstalledApp</c> in each agent's <c>system_info.rs</c>.
/// </summary>
public record ApplicationEntry(
    string Name,
    string Version,
    string? PackageManager = null,
    string? ApplicationIdentifier = null,
    string? AvailableVersion = null,
    bool? UpdateAvailable = null);

public record RegisterApplicationsResult(Guid HostId, int ApplicationCount);
