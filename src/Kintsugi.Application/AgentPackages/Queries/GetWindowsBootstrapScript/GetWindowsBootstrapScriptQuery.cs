using MediatR;

namespace Kintsugi.Application.AgentPackages.Queries.GetWindowsBootstrapScript;

/// <summary>
/// The silent PowerShell installer for the Windows build published here — what an administrator
/// pastes into CrowdStrike to install the agent across a fleet. See <see cref="WindowsBootstrapScript"/>
/// for what the script is and why its pinned checksum is the whole point.
/// </summary>
/// <param name="ApiBaseUrl">nginx's own address and port, resolved server-side by
/// <c>AdminClientsController</c>. Passed in rather than read here for the same reason the import
/// command takes it: a base URL is what gets baked into what agents are told to trust, so it must
/// never come from the client.</param>
public record GetWindowsBootstrapScriptQuery(string ApiBaseUrl) : IRequest<WindowsBootstrapScriptDto>;

/// <param name="Script">The rendered script, or null when one cannot be rendered.</param>
/// <param name="Version">The agent version the script installs.</param>
/// <param name="Sha256">The checksum the script pins, shown beside it so a reader can see what is
/// being vouched for without reading the body.</param>
/// <param name="UnavailableReason">Why no script could be rendered. Never set alongside
/// <paramref name="Script"/>; exactly one of the two is always present, so the screen never has to
/// choose between showing an empty box and showing nothing.</param>
public record WindowsBootstrapScriptDto(
    string? Script,
    string? Version,
    string? Sha256,
    string? UnavailableReason);
