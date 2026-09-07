using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Auditing;

/// <summary>
/// The auditing settings as the settings page sees them. The secret is never returned;
/// <see cref="HasSecret"/> reports whether one is stored, which is what lets the form honestly
/// offer "leave blank to keep the existing one" — the same contract every other settings DTO has.
/// </summary>
public record AuditSettingsDto(
    AuditProvider Provider,
    bool IsEnabled,
    string? Endpoint,
    string? Region,
    string? ClientId,
    bool HasSecret,
    string? TenantId,
    string? ProjectId,
    string? LogGroup,
    string? DataCollectionRuleId,
    string? Stream,
    string? Index)
{
    public static AuditSettingsDto FromEntity(AuditSettings entity) =>
        new(
            entity.Provider,
            entity.IsEnabled,
            entity.Endpoint,
            entity.Region,
            entity.ClientId,
            !string.IsNullOrEmpty(entity.Secret),
            entity.TenantId,
            entity.ProjectId,
            entity.LogGroup,
            entity.DataCollectionRuleId,
            entity.Stream,
            entity.Index);

    public static AuditSettingsDto NotConfigured() =>
        new(AuditProvider.Datadog, IsEnabled: false, null, null, null, HasSecret: false, null, null, null, null, null, null);
}
