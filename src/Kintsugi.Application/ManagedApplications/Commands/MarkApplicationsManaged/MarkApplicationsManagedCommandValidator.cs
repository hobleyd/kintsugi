using FluentValidation;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.ManagedApplications.Commands.MarkApplicationsManaged;

public class MarkApplicationsManagedCommandValidator : AbstractValidator<MarkApplicationsManagedCommand>
{
    /// <summary>More than this in one request is a list somebody should be reading, not a click.</summary>
    public const int MaxNames = 500;

    public MarkApplicationsManagedCommandValidator()
    {
        RuleFor(x => x.Names).NotEmpty().WithMessage("Choose at least one application.");
        RuleFor(x => x.Names.Count).LessThanOrEqualTo(MaxNames).When(x => x.Names is not null)
            .WithMessage($"At most {MaxNames} applications can be marked at once.");
        RuleForEach(x => x.Names).NotEmpty().MaximumLength(ManagedApplication.MaxNameLength);
        RuleFor(x => x.Owner).NotEmpty().WithMessage("Say who patches it.").MaximumLength(ManagedApplication.MaxOwnerLength);
    }
}
