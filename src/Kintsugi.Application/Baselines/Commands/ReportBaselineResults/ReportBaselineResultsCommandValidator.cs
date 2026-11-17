using FluentValidation;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Baselines.Commands.ReportBaselineResults;

public class ReportBaselineResultsCommandValidator : AbstractValidator<ReportBaselineResultsCommand>
{
    public const int MaxBaselines = 50;

    public ReportBaselineResultsCommandValidator()
    {
        RuleFor(c => c.SerialNumber).NotEmpty().MaximumLength(255);
        RuleFor(c => c.Baselines).NotNull().Must(b => b is null || b.Count <= MaxBaselines);
        RuleForEach(c => c.Baselines).ChildRules(baseline =>
        {
            baseline.RuleFor(b => b.VersionId).NotEmpty();
            baseline.RuleFor(b => b.Items).NotNull().Must(i => i is null || i.Count <= BaselineItemRules.MaxItems);
            baseline.RuleFor(b => b.Facts).NotNull().Must(f => f is null || f.Count <= BaselineItemRules.MaxItems);
            baseline.RuleForEach(b => b.Items).ChildRules(item =>
            {
                item.RuleFor(i => i.Key).NotEmpty().MaximumLength(BaselineItemRules.MaxKeyLength);
                // Names, never ordinals: see AgentItemStateDto.
                item.RuleFor(i => i.State).Must(s => Enum.GetNames<BaselineItemState>().Contains(s))
                    .WithMessage("State is one of: " + string.Join(", ", Enum.GetNames<BaselineItemState>()) + ".");
                // The agent truncates below these (baseline::MAX_OBSERVED_CHARS / MAX_DETAIL_CHARS), so a
                // report is never refused for one long value. Raise these first, never the agent's.
                item.RuleFor(i => i.Observed).MaximumLength(HostBaselineItemState.MaxObservedLength);
                item.RuleFor(i => i.Detail).MaximumLength(HostBaselineItemState.MaxDetailLength);
            });
            baseline.RuleForEach(b => b.Facts).ChildRules(fact =>
            {
                fact.RuleFor(f => f.Key).NotEmpty().MaximumLength(BaselineItemRules.MaxKeyLength);
                fact.RuleFor(f => f.Value).MaximumLength(HostFact.MaxValueLength);
            });
        });
    }
}
