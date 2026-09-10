using FluentValidation;

namespace Kintsugi.Application.PatchFailures.Commands.ReportPatchFailure;

public class ReportPatchFailureCommandValidator : AbstractValidator<ReportPatchFailureCommand>
{
    /// <summary>
    /// The ceiling on <see cref="ReportPatchFailureCommand.Details"/>, matching the column width in
    /// <c>PatchFailureConfiguration</c>.
    /// </summary>
    /// <remarks>
    /// This number is one end of a coupling with each agent's <c>upgrade::MAX_REPORTED_FAILURE_BYTES</c>,
    /// which truncates to a quarter of it before sending. It has to stay the larger of the two: a
    /// script's captured stderr is unbounded, and a validator that rejected what an agent actually
    /// sends would answer the report with a 400 and lose the failure entirely — silently, and
    /// precisely for the noisiest failures, which are the ones most worth reading. Raise this before
    /// raising the agents' figure, never after.
    /// </remarks>
    public const int MaxDetailsLength = 16000;

    public ReportPatchFailureCommandValidator()
    {
        RuleFor(x => x.SerialNumber).NotEmpty().MaximumLength(128);
        RuleFor(x => x.ApplicationName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.InstalledVersion).MaximumLength(64);
        RuleFor(x => x.AttemptedVersion).MaximumLength(64);
        RuleFor(x => x.Details).NotEmpty().MaximumLength(MaxDetailsLength);
        RuleFor(x => x.FailedUtc).NotEqual(default(DateTimeOffset));
    }
}
