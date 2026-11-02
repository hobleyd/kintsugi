using MediatR;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Authentication.Commands.UpdateAuthenticationSettings;

/// <summary>A blank <paramref name="ClientSecret"/> leaves any previously stored secret untouched.
/// A null <paramref name="AdminEmails"/> leaves the stored allowlist untouched; an empty list clears it.</summary>
public record UpdateAuthenticationSettingsCommand(
    AuthProvider Provider,
    string? ClientId,
    string? ClientSecret,
    string? Authority,
    string? TenantId,
    string? HostedDomain,
    bool IsEnabled,
    IReadOnlyList<string>? AdminEmails = null) : IRequest<AuthenticationSettingsDto>;
