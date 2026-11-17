using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.AiSettings.Queries.GetAiUsage;

public class GetAiUsageQueryHandler : IRequestHandler<GetAiUsageQuery, AiUsageDto>
{
    /// <summary>How many days the daily chart covers.</summary>
    public const int ChartDays = 30;

    private readonly IAiUsageRepository _repository;

    public GetAiUsageQueryHandler(IAiUsageRepository repository)
    {
        _repository = repository;
    }

    public async Task<AiUsageDto> Handle(GetAiUsageQuery request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetBudgetAsync(cancellationToken) ?? AiBudgetSettings.CreateDefault();
        var now = DateTimeOffset.UtcNow;
        var monthStart = AiBudgetStatusBuilder.MonthStart(now);
        var chartStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-(ChartDays - 1));

        var since = chartStart < monthStart ? chartStart : monthStart;
        var records = await _repository.GetSinceAsync(since, cancellationToken);
        var month = records.Where(r => r.OccurredUtc >= monthStart).ToList();

        return Build(settings, month, records.Where(r => r.OccurredUtc >= chartStart).ToList(), monthStart, chartStart, now);
    }

    /// <summary>The screen's numbers from the month's records and the chart window's. Pure, so the
    /// rules (what counts as a priced model, how days are filled in) can be tested.</summary>
    public static AiUsageDto Build(
        AiBudgetSettings settings,
        IReadOnlyList<AiUsageRecord> month,
        IReadOnlyList<AiUsageRecord> chartWindow,
        DateTimeOffset monthStart,
        DateTimeOffset chartStart,
        DateTimeOffset now)
    {
        var status = AiBudgetStatusBuilder.Build(settings, AiSpendTotals.From(month), now);

        var byFeature = month
            .GroupBy(r => r.Feature)
            .Select(g => new AiUsageFeatureDto(g.Key, g.Count(), g.Sum(r => r.InputTokens), g.Sum(r => r.OutputTokens), g.Sum(r => r.EstimatedCostUsd ?? 0m)))
            .OrderByDescending(f => f.CostUsd).ThenByDescending(f => f.Calls)
            .ToList();

        var byModel = month
            .GroupBy(r => (r.ConnectionName, r.Model))
            .Select(g => new AiUsageModelDto(
                g.Key.ConnectionName, g.Key.Model, g.Count(), g.Sum(r => r.InputTokens), g.Sum(r => r.OutputTokens),
                g.Sum(r => r.EstimatedCostUsd ?? 0m),
                PriceKnown: g.All(r => r.EstimatedCostUsd is not null)))
            .OrderByDescending(m => m.CostUsd).ThenByDescending(m => m.Calls)
            .ToList();

        var bySubject = month
            .Where(r => r.Subject is not null)
            .GroupBy(r => r.Subject!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AiUsageSubjectDto(g.Key, g.Count(), g.Sum(r => r.InputTokens + r.OutputTokens), g.Sum(r => r.EstimatedCostUsd ?? 0m)))
            .OrderByDescending(s => s.CostUsd).ThenByDescending(s => s.Tokens)
            .Take(10)
            .ToList();

        // Every day in the window, including the quiet ones, so the chart's gaps are real gaps.
        var byDay = chartWindow.GroupBy(r => DateOnly.FromDateTime(r.OccurredUtc.UtcDateTime)).ToDictionary(g => g.Key);
        var days = Enumerable.Range(0, ChartDays)
            .Select(i => DateOnly.FromDateTime(chartStart.UtcDateTime.AddDays(i)))
            .Select(day => byDay.TryGetValue(day, out var g)
                ? new AiUsageDayDto(day, g.Sum(r => r.InputTokens + r.OutputTokens), g.Sum(r => r.EstimatedCostUsd ?? 0m))
                : new AiUsageDayDto(day, 0, 0m))
            .ToList();

        return new AiUsageDto(
            status,
            new AiBudgetSettingsDto(settings.BudgetAmount, settings.BudgetCurrency, settings.UsdToAudRate, settings.WarnPercent, settings.StopAtLimit),
            monthStart,
            monthStart.AddMonths(1),
            month.Sum(r => r.InputTokens + r.OutputTokens),
            byFeature,
            byModel,
            bySubject,
            days);
    }
}
