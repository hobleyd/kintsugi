using FluentValidation;

namespace Kintsugi.Application.ForcedPatchRuns.Commands.RequestForcedPatchRuns;

public class RequestForcedPatchRunsCommandValidator : AbstractValidator<RequestForcedPatchRunsCommand>
{
    /// <summary>
    /// The most hosts one press may name. Not a fleet-size limit — it is what stops a malformed or
    /// hostile body from opening an unbounded number of rows in a single call, and it sits well
    /// above any plausible fleet so a legitimate "all Windows hosts" is never the thing it rejects.
    /// </summary>
    public const int MaxHostNames = 5000;

    public RequestForcedPatchRunsCommandValidator()
    {
        RuleFor(x => x.ApplicationName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Platform).NotEmpty().MaximumLength(64);
        RuleFor(x => x.HostNames).NotEmpty().WithMessage("Forcing a patch run needs at least one host to run it on.");
        RuleFor(x => x.HostNames).Must(names => names is null || names.Count <= MaxHostNames)
            .WithMessage($"No more than {MaxHostNames} hosts can be forced in one request.");
        RuleForEach(x => x.HostNames).NotEmpty().MaximumLength(255);
    }
}
