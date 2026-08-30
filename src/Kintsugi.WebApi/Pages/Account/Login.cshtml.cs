using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Kintsugi.Application.Authentication.Queries.GetAuthenticationSettings;
using Kintsugi.Domain.Enums;

namespace Kintsugi.WebApi.Pages.Account;

public class LoginModel : PageModel
{
    private readonly ISender _sender;

    public LoginModel(ISender sender)
    {
        _sender = sender;
    }

    public string? ReturnUrl { get; private set; }
    public string ProviderDisplayName { get; private set; } = "your identity provider";
    public bool IsConfigured { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl, CancellationToken cancellationToken)
    {
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;

        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(ReturnUrl ?? "/hosts");
        }

        var settings = await _sender.Send(new GetAuthenticationSettingsQuery(), cancellationToken);
        IsConfigured = settings.IsEnabled && settings.HasClientSecret;
        ProviderDisplayName = settings.Provider switch
        {
            AuthProvider.GoogleWorkspace => "Google Workspace",
            AuthProvider.MicrosoftEntra => "Microsoft Entra",
            AuthProvider.Clerk => "Clerk",
            _ => "single sign-on"
        };

        return Page();
    }

    public IActionResult OnGetChallenge(string? returnUrl)
    {
        var safeReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : "/hosts";
        return Challenge(new AuthenticationProperties { RedirectUri = safeReturnUrl }, OpenIdConnectDefaults.AuthenticationScheme);
    }
}
