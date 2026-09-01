using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Kintsugi.Application.GitHub;
using Kintsugi.Application.GitHub.Commands.UpdateGitHubSettings;
using Kintsugi.Application.GitHub.Queries.GetGitHubSettings;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.WebApi.Pages.Settings;

public class GitHubModel : PageModel
{
    private readonly ISender _sender;

    public GitHubModel(ISender sender)
    {
        _sender = sender;
    }

    [BindProperty]
    public GitHubInputModel Input { get; set; } = new();

    /// <summary>Whether a token is stored, so the page can say "leave blank to keep the existing
    /// one" honestly. The tokens themselves never reach the browser — see <see cref="GitHubSettingsDto"/>.</summary>
    public GitHubSettingsDto Settings { get; private set; } = default!;

    public bool SaveSucceeded { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        Input = new GitHubInputModel
        {
            // The effective values, defaults included, rather than blanks — the operator should see
            // which repositories this server is actually pointed at.
            AgentPackageRepository = Settings.AgentPackageRepository,
            ScriptApprovalRepository = Settings.ScriptApprovalRepository,
        };
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        try
        {
            Settings = await _sender.Send(
                new UpdateGitHubSettingsCommand(
                    Input.AgentPackageRepository,
                    Input.ScriptApprovalRepository,
                    Input.ApiToken,
                    Input.ClearApiToken,
                    Input.ScriptApprovalToken,
                    Input.ClearScriptApprovalToken),
                cancellationToken);

            SaveSucceeded = true;

            // Never echo a submitted token back into the form, even on success — it would sit in the
            // rendered HTML of a page the browser may cache or restore.
            Input = new GitHubInputModel
            {
                AgentPackageRepository = Settings.AgentPackageRepository,
                ScriptApprovalRepository = Settings.ScriptApprovalRepository,
            };
        }
        catch (Exception ex) when (ex is ValidationException or DomainException)
        {
            foreach (var message in ex is ValidationException validation
                ? validation.Errors.Select(e => e.ErrorMessage)
                : new[] { ex.Message })
            {
                ModelState.AddModelError(string.Empty, message);
            }

            await LoadAsync(cancellationToken);
            Input.ApiToken = null;
            Input.ScriptApprovalToken = null;
        }

        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken) =>
        Settings = await _sender.Send(new GetGitHubSettingsQuery(), cancellationToken);
}

public class GitHubInputModel
{
    public string? AgentPackageRepository { get; set; }

    public string? ScriptApprovalRepository { get; set; }

    /// <summary>Blank means "keep whatever is stored" — the form was never given the real value, so
    /// it has nothing to send back unchanged.</summary>
    public string? ApiToken { get; set; }

    public bool ClearApiToken { get; set; }

    public string? ScriptApprovalToken { get; set; }

    public bool ClearScriptApprovalToken { get; set; }
}
