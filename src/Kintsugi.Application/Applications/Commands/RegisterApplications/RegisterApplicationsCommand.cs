using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Applications.Commands.RegisterApplications;

/// <summary>
/// Registers the full set of applications installed on a host, identified by
/// its serial number. Replaces any previously reported list for that host
/// (agents report a full inventory snapshot, not incremental changes).
/// </summary>
/// <param name="Packages">
/// The host's operating-system packages — dpkg or rpm entries, sent by the Linux agent only.
/// Optional, so the macOS and Windows agents and any Linux agent predating the field are
/// unaffected.
/// </param>
/// <remarks>
/// <b><paramref name="Packages"/> is inventory for vulnerability assessment and nothing else.</b>
/// It lands in its own table and is invisible to the Applications screen, to
/// <c>upgrade_paths</c>, to the AI and to every patch cycle — see
/// <see cref="Kintsugi.Domain.Entities.InstalledPackage"/> for why that is a separate table
/// rather than a flag. Reporting it does not make apt or dnf a package manager this system
/// patches through; that decision is unchanged and is about something else entirely.
/// </remarks>
public record RegisterApplicationsCommand(
    string SerialNumber,
    IReadOnlyList<ApplicationEntry> Applications,
    IReadOnlyList<PackageEntry>? Packages = null)
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

/// <summary>
/// One reported operating-system package.
/// </summary>
/// <param name="Name">
/// The <b>source</b> package name — <c>openssl</c>, not <c>libssl3</c>; <c>glibc</c>, not
/// <c>libc6</c>. Distributions track CVEs against source packages and so does OSV: verified
/// against the live API, <c>libssl3</c> answers 0 vulnerabilities on Ubuntu 22.04 where
/// <c>openssl</c> answers 48. Sending binary names would silently under-report nearly
/// everything, so the agent reads <c>${source:Package}</c> from dpkg and derives the name from
/// <c>%{SOURCERPM}</c> for rpm.
/// </param>
/// <param name="Version">The distribution's own version, packaging revision included — that
/// revision is what carries a backported fix, and is the difference between 48 vulnerabilities
/// and 36 for the same upstream 3.0.2.</param>
/// <param name="Source">Which packaging system reported it: <c>dpkg</c> or <c>rpm</c>.</param>
public record PackageEntry(string Name, string Version, string Source);

/// <param name="PackageCount">How many operating-system packages were recorded. Reported
/// separately from <paramref name="ApplicationCount"/> because they are separate inventories with
/// separate purposes, and an agent that has started sending packages should be able to see that
/// they arrived.</param>
public record RegisterApplicationsResult(Guid HostId, int ApplicationCount, int PackageCount = 0);
