using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Kintsugi.Application.PatchingPolicy.Commands.UpdatePatchingPolicySettings;
using Kintsugi.Application.PatchingPolicy.Queries.GetPatchingPolicySettings;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.WebApi.Pages.Settings;

public class PatchingPolicyModel : PageModel
{
    private readonly ISender _sender;

    public PatchingPolicyModel(ISender sender)
    {
        _sender = sender;
    }

    [BindProperty]
    public PatchingPolicyInputModel Input { get; set; } = new();

    public bool SaveSucceeded { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var policy = await _sender.Send(new GetPatchingPolicySettingsQuery(), cancellationToken);
        Input = new PatchingPolicyInputModel
        {
            IntervalValue = policy.IntervalValue,
            IntervalUnit = policy.IntervalUnit,
            DelayValue = policy.DelayValue,
            DelayUnit = policy.DelayUnit,
            MaxDelayCount = policy.MaxDelayCount
        };
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _sender.Send(
                new UpdatePatchingPolicySettingsCommand(
                    Input.IntervalValue, Input.IntervalUnit, Input.DelayValue, Input.DelayUnit, Input.MaxDelayCount),
                cancellationToken);

            Input = new PatchingPolicyInputModel
            {
                IntervalValue = result.IntervalValue,
                IntervalUnit = result.IntervalUnit,
                DelayValue = result.DelayValue,
                DelayUnit = result.DelayUnit,
                MaxDelayCount = result.MaxDelayCount
            };
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

    public class PatchingPolicyInputModel
    {
        public int IntervalValue { get; set; } = 7;
        public PatchingTimeUnit IntervalUnit { get; set; } = PatchingTimeUnit.Days;
        public int DelayValue { get; set; } = 1;
        public PatchingTimeUnit DelayUnit { get; set; } = PatchingTimeUnit.Days;
        public int MaxDelayCount { get; set; } = 3;
    }
}
