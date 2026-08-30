using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Kintsugi.Application.AiSettings.Commands.UpdateAiAgentSettings;
using Kintsugi.Application.AiSettings.Queries.GetAiAgentSettings;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.WebApi.Pages.Settings;

public class AiAgentModel : PageModel
{
    private readonly ISender _sender;

    public AiAgentModel(ISender sender)
    {
        _sender = sender;
    }

    [BindProperty]
    public SettingsInputModel Input { get; set; } = new();

    public bool HasApiKey { get; private set; }
    public bool SaveSucceeded { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await _sender.Send(new GetAiAgentSettingsQuery(), cancellationToken);
        Input = new SettingsInputModel
        {
            Provider = settings.Provider,
            Model = settings.Model,
            BaseUrl = settings.BaseUrl,
            IsEnabled = settings.IsEnabled
        };
        HasApiKey = settings.HasApiKey;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        // Fetched up front so the "leave blank to keep current key" hint still renders correctly if the save below fails.
        HasApiKey = (await _sender.Send(new GetAiAgentSettingsQuery(), cancellationToken)).HasApiKey;

        try
        {
            var result = await _sender.Send(
                new UpdateAiAgentSettingsCommand(Input.Provider, Input.ApiKey, Input.BaseUrl, Input.Model, Input.IsEnabled),
                cancellationToken);

            HasApiKey = result.HasApiKey;
            SaveSucceeded = true;
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
        public AiProvider Provider { get; set; } = AiProvider.Anthropic;
        public string? ApiKey { get; set; }
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
        public bool IsEnabled { get; set; }
    }
}
