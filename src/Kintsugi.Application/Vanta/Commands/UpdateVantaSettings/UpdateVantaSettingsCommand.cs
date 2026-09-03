using MediatR;

namespace Kintsugi.Application.Vanta.Commands.UpdateVantaSettings;

/// <summary>
/// Saves the Vanta settings page.
/// </summary>
/// <param name="ClientSecret">Blank keeps whatever is stored — the page never received the real
/// value, so it cannot send it back unchanged. Use <paramref name="ClearClientSecret"/> to remove
/// one.</param>
/// <param name="ApiBaseUrl">Blank means the default, Vanta's commercial host. A FedRAMP tenant sets
/// <c>https://api.vanta-gov.com</c>.</param>
/// <param name="ConsoleBaseUrl">This server's own browser-facing address, used to build the
/// <c>externalUrl</c> on every synced record. Must be https — Vanta requires it.</param>
public record UpdateVantaSettingsCommand(
    bool Enabled,
    string? ClientId,
    string? ClientSecret,
    bool ClearClientSecret,
    string? ApiBaseUrl,
    string? VulnerableComponentResourceId,
    string? PackageVulnerabilityResourceId,
    string? ConsoleBaseUrl,
    double? Severity,
    int? SyncIntervalHours) : IRequest<VantaSettingsDto>;
