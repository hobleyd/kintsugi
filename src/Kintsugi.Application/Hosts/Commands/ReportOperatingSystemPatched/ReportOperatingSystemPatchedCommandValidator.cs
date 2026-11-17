using FluentValidation;

namespace Kintsugi.Application.Hosts.Commands.ReportOperatingSystemPatched;

public class ReportOperatingSystemPatchedCommandValidator : AbstractValidator<ReportOperatingSystemPatchedCommand>
{
    public ReportOperatingSystemPatchedCommandValidator()
    {
        RuleFor(x => x.SerialNumber).NotEmpty().MaximumLength(128);
        // No length rule on RestartBecause, for the same reason as CreateHostCommandValidator:
        // Host.RecordRestartState cuts it to the column rather than refusing the report.
    }
}
