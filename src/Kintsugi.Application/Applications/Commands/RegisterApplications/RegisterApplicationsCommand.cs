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
/// registry, or a winget/Chocolatey package id. Null for Homebrew-sourced
/// entries, which have no identifier separate from their name.
/// <see cref="AvailableVersion"/> is the latest version known to be available
/// independently of any upgrade research (a package manager's own catalog
/// version) — when present alongside a <see cref="PackageManager"/> this system
/// recognizes (see <c>PackageManagerCatalog</c>), it seeds that application's
/// <see cref="Kintsugi.Domain.Entities.UpgradePath"/> directly, without waiting
/// on AI research.
/// </summary>
public record ApplicationEntry(
    string Name,
    string Version,
    string? PackageManager = null,
    string? ApplicationIdentifier = null,
    string? AvailableVersion = null);

public record RegisterApplicationsResult(Guid HostId, int ApplicationCount);
