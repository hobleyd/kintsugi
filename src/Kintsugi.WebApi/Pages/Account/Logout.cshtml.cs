using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kintsugi.WebApi.Pages.Account;

public class LogoutModel : PageModel
{
    public IActionResult OnGet() => LocalRedirect("/hosts");

    public IActionResult OnPost() =>
        SignOut(
            new AuthenticationProperties { RedirectUri = "/account/login" },
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme);
}
