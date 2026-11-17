using FluentValidation;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.DeviceHealth.Commands.ReportHostHealthEvents;

public class ReportHostHealthEventsCommandValidator : AbstractValidator<ReportHostHealthEventsCommand>
{
    public const int MaxEvents = 100;
    public const int MaxSourceLength = 255;
    public const int MaxDetailLength = 1000;

    /// <summary>Agents look back 30 days and the page assesses 30 days, so an older event is of no use and
    /// only lets a host store history that nothing reads.</summary>
    public const int MaxAgeDays = 35;

    /// <summary>The kinds an agent may report: only the health ones. An agent cannot write a "patched" or
    /// "Patch now" event to a timeline by choosing its kind.</summary>
    public static readonly IReadOnlySet<string> AllowedKinds = new HashSet<string>
    {
        nameof(HostEventKind.UnexpectedShutdown),
        nameof(HostEventKind.HardwareErrorFatal),
        nameof(HostEventKind.HardwareErrorCorrected),
    };

    public ReportHostHealthEventsCommandValidator()
    {
        RuleFor(x => x.SerialNumber).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Events).Cascade(CascadeMode.Stop).NotNull().Must(e => e.Count <= MaxEvents)
            .WithMessage($"At most {MaxEvents} events in one report.");
        RuleForEach(x => x.Events).ChildRules(e =>
        {
            e.RuleFor(x => x.Kind).Must(k => AllowedKinds.Contains(k)).WithMessage("Unknown health event kind.");
            e.RuleFor(x => x.Source).NotEmpty().MaximumLength(MaxSourceLength);
            e.RuleFor(x => x.Detail).MaximumLength(MaxDetailLength);
            e.RuleFor(x => x.OccurredUtc)
                .Must(t => t <= DateTimeOffset.UtcNow.AddMinutes(10)).WithMessage("An event cannot be in the future.")
                .Must(t => t >= DateTimeOffset.UtcNow.AddDays(-MaxAgeDays)).WithMessage($"An event cannot be older than {MaxAgeDays} days.");
        });
    }
}
