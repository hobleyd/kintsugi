using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.AiSettings;

/// <summary>Where the month's spending stands against the limit.</summary>
/// <param name="SpentUsd">Estimated dollars spent so far this month.</param>
/// <param name="BudgetUsd">The limit in dollars; null when there is none.</param>
/// <param name="Percent">Spent as a share of the limit, uncapped; 0 when there is no limit.</param>
/// <param name="WarnReached">Whether the warning level has been passed.</param>
/// <param name="LimitReached">Whether the limit has been reached, whether or not the stop is on.</param>
/// <param name="Blocked">Whether new AI calls are being refused: the limit is reached and the stop is on.</param>
/// <param name="Message">Plain words for the person who is blocked; null when nothing is.</param>
/// <param name="UnpricedCalls">Calls this month to a model the catalogue had no price for.</param>
/// <param name="UnpricedEstimateUsd">The part of <paramref name="SpentUsd"/> that is those calls
/// counted at <see cref="AiPricing.FallbackInputPerMillion"/>/<see cref="AiPricing.FallbackOutputPerMillion"/>.</param>
public record AiBudgetStatusDto(
    decimal SpentUsd,
    decimal? BudgetUsd,
    int Percent,
    bool WarnReached,
    bool LimitReached,
    bool Blocked,
    string? Message,
    int UnpricedCalls = 0,
    decimal UnpricedEstimateUsd = 0m);

/// <summary>A month's spend as the budget counts it: the priced calls' recorded costs, plus the
/// tokens of the calls nobody could price, which are charged at the fallback rate.</summary>
public record AiSpendTotals(decimal PricedUsd, int UnpricedCalls, long UnpricedInputTokens, long UnpricedOutputTokens)
{
    public static readonly AiSpendTotals None = new(0m, 0, 0, 0);

    public decimal UnpricedEstimateUsd => AiPricing.FallbackEstimate(UnpricedInputTokens, UnpricedOutputTokens);

    public decimal TotalUsd => PricedUsd + UnpricedEstimateUsd;

    /// <summary>The same totals from records already in memory — the AI Usage screen's path, which
    /// must agree to the cent with the budget service's database query.</summary>
    public static AiSpendTotals From(IEnumerable<AiUsageRecord> records)
    {
        var list = records as IReadOnlyCollection<AiUsageRecord> ?? records.ToList();
        var unpriced = list.Where(r => r.EstimatedCostUsd is null).ToList();
        return new AiSpendTotals(
            list.Sum(r => r.EstimatedCostUsd ?? 0m),
            unpriced.Count,
            unpriced.Sum(r => r.InputTokens),
            unpriced.Sum(r => r.OutputTokens));
    }
}

public record AiBudgetSettingsDto(
    decimal? BudgetAmount,
    string BudgetCurrency,
    decimal UsdToAudRate,
    int WarnPercent,
    bool StopAtLimit);

public record AiUsageFeatureDto(AiFeature Feature, int Calls, long InputTokens, long OutputTokens, decimal CostUsd);

public record AiUsageModelDto(string ConnectionName, string Model, int Calls, long InputTokens, long OutputTokens, decimal CostUsd, bool PriceKnown);

public record AiUsageSubjectDto(string Subject, int Calls, long Tokens, decimal CostUsd);

public record AiUsageDayDto(DateOnly Day, long Tokens, decimal CostUsd);

/// <summary>What the AI Usage screen shows: this month against the limit, the settings, and the
/// breakdowns. Costs are estimates — see <c>AiUsageRecord</c>.</summary>
public record AiUsageDto(
    AiBudgetStatusDto Status,
    AiBudgetSettingsDto Settings,
    DateTimeOffset MonthStartUtc,
    DateTimeOffset NextMonthStartUtc,
    long TotalTokens,
    IReadOnlyList<AiUsageFeatureDto> ByFeature,
    IReadOnlyList<AiUsageModelDto> ByModel,
    IReadOnlyList<AiUsageSubjectDto> BySubject,
    IReadOnlyList<AiUsageDayDto> Days);
