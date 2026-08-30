using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kintsugi.WebApi.Pages;

public class SettingsModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Settings/AiAgent");
}
