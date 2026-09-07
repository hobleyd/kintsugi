using MediatR;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Auditing.Commands.UpdateAuditSettings;

/// <summary>
/// Saves the Auditing settings page. Which fields a provider reads is documented on
/// <see cref="Domain.Entities.AuditSettings"/>; the rest are ignored and stored as null.
/// </summary>
/// <param name="Secret">Blank keeps whatever is stored for the <em>same</em> provider — the page
/// never received the real value, so it cannot send it back unchanged. Changing the provider drops
/// the stored secret regardless. Use <paramref name="ClearSecret"/> to remove one from a provider
/// whose secret is optional.</param>
public record UpdateAuditSettingsCommand(
    AuditProvider Provider,
    bool IsEnabled,
    string? Endpoint,
    string? Region,
    string? ClientId,
    string? Secret,
    bool ClearSecret,
    string? TenantId,
    string? ProjectId,
    string? LogGroup,
    string? DataCollectionRuleId,
    string? Stream,
    string? Index) : IRequest<AuditSettingsDto>;
