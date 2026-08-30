using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Kintsugi.Application.Authentication.Commands.UpdateAuthenticationSettings;
using Kintsugi.Application.Authentication.Queries.GetAuthenticationSettings;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.WebApi.Pages.Settings;

public class AuthenticationModel : PageModel
{
    private readonly ISender _sender;
    private readonly IOptionsMonitorCache<OpenIdConnectOptions> _oidcOptionsCache;

    public AuthenticationModel(ISender sender, IOptionsMonitorCache<OpenIdConnectOptions> oidcOptionsCache)
    {
        _sender = sender;
        _oidcOptionsCache = oidcOptionsCache;
    }

    [BindProperty]
    public SettingsInputModel Input { get; set; } = new();

    public bool HasClientSecret { get; private set; }
    public bool SaveSucceeded { get; private set; }

    public string CallbackUrl => $"{Request.Scheme}://{Request.Host}/signin-oidc";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await _sender.Send(new GetAuthenticationSettingsQuery(), cancellationToken);
        Input = new SettingsInputModel
        {
            Provider = settings.Provider,
            ClientId = settings.ClientId,
            Authority = settings.Authority,
            TenantId = settings.TenantId,
            HostedDomain = settings.HostedDomain,
            IsEnabled = settings.IsEnabled
        };
        HasClientSecret = settings.HasClientSecret;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        // Fetched up front so the "leave blank to keep current secret" hint still renders correctly if the save below fails.
        HasClientSecret = (await _sender.Send(new GetAuthenticationSettingsQuery(), cancellationToken)).HasClientSecret;

        try
        {
            var result = await _sender.Send(
                new UpdateAuthenticationSettingsCommand(
                    Input.Provider, Input.ClientId, Input.ClientSecret, Input.Authority, Input.TenantId, Input.HostedDomain, Input.IsEnabled),
                cancellationToken);

            HasClientSecret = result.HasClientSecret;
            SaveSucceeded = true;

            // The OIDC handler's options are cached until invalidated (see
            // DynamicOpenIdConnectOptionsConfigurator) — clear the cache so the next sign-in
            // attempt picks up what was just saved instead of a stale provider/secret.
            _oidcOptionsCache.TryRemove(OpenIdConnectDefaults.AuthenticationScheme);
        }
        catch (ValidationException ex)
        {
            foreach (var error in ex.Errors)
            {
                ModelState.AddModelError(string.Empty, error.ErrorMessage);
            }
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }

        return Page();
    }

    public class SettingsInputModel
    {
        public AuthProvider Provider { get; set; } = AuthProvider.GoogleWorkspace;
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
        public string? Authority { get; set; }
        public string? TenantId { get; set; }
        public string? HostedDomain { get; set; }
        public bool IsEnabled { get; set; }
    }
}
