using FluentValidation;
using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.Baselines.Commands.AssignBaseline;

public class AssignBaselineCommandValidator : AbstractValidator<AssignBaselineCommand>
{
    /// <summary>Same ceiling, for the same reason, as <c>RequestForcedPatchRunsCommandValidator.MaxHostNames</c>.</summary>
    public const int MaxHostNames = 5000;

    public AssignBaselineCommandValidator()
    {
        RuleFor(c => c.BaselineId).NotEmpty();
        RuleFor(c => c.HostNames).NotNull().Must(n => n is null || n.Count <= MaxHostNames)
            .WithMessage($"No more than {MaxHostNames} hosts can be named.");
        RuleForEach(c => c.HostNames).NotEmpty().MaximumLength(255);
    }
}
