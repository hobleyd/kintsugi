using FluentValidation;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.HostActivity.Commands.UpdateHostActivityRetention;

public class UpdateHostActivityRetentionCommandValidator : AbstractValidator<UpdateHostActivityRetentionCommand>
{
    public UpdateHostActivityRetentionCommandValidator()
    {
        RuleFor(x => x.RetentionMonths).InclusiveBetween(1, HostActivitySettings.MaxRetentionMonths);
    }
}
