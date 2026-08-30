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
/// <see cref="ApplicationIdentifier"/> is the app bundle's CFBundleIdentifier
/// (e.g. "com.example.MyApp"); null for Homebrew-sourced entries.
/// <see cref="AvailableVersion"/> is the latest version known to be available
/// independently of any upgrade research (currently: a Homebrew formula/cask's
/// catalog version) — when present alongside <see cref="PackageManager"/>, it
/// seeds that application's <see cref="Kintsugi.Domain.Entities.UpgradePath"/>
/// directly, without waiting on AI research.
/// </summary>
public record ApplicationEntry(
    string Name,
    string Version,
    string? PackageManager = null,
    string? ApplicationIdentifier = null,
    string? AvailableVersion = null);

public record RegisterApplicationsResult(Guid HostId, int ApplicationCount);
