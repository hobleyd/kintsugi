using Kintsugi.Application.AiSettings;
using Kintsugi.Application.AiSettings.Queries.GetAiUsage;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Application;

public class AiBudgetTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    private static AiBudgetSettings Budget(decimal? amount, string currency = "USD", decimal rate = 1.55m, int warn = 80, bool stop = true)
    {
        var settings = AiBudgetSettings.CreateDefault();
        settings.Update(amount, currency, rate, warn, stop);
        return settings;
    }

    private static AiUsageRecord Record(AiFeature feature, string connection, string model, string? subject, long input, long output, decimal? cost, DateTimeOffset? at = null) =>
        AiUsageRecord.Create(at ?? Now, feature, connection, model, subject, input, output, cost);

    [Fact]
    public void WithNoBudgetNothingIsEverBlocked()
    {
        var status = AiBudgetStatusBuilder.Build(AiBudgetSettings.CreateDefault(), 9999m, Now);

        Assert.Null(status.BudgetUsd);
        Assert.False(status.Blocked);
        Assert.False(status.LimitReached);
        Assert.Null(status.Message);
    }

    [Theory]
    [InlineData(10, 20, false, false, false)]
    [InlineData(40, 80, true, false, false)]
    [InlineData(49.99, 99, true, false, false)]
    [InlineData(50, 100, true, true, true)]
    [InlineData(80, 160, true, true, true)]
    public void TheThresholdsAreTheWarnLevelAndTheLimit(double spent, int percent, bool warn, bool limit, bool blocked)
    {
        var status = AiBudgetStatusBuilder.Build(Budget(50m), (decimal)spent, Now);

        Assert.Equal(percent, status.Percent);
        Assert.Equal(warn, status.WarnReached);
        Assert.Equal(limit, status.LimitReached);
        Assert.Equal(blocked, status.Blocked);
    }

    [Fact]
    public void WithTheStopOffTheLimitIsReachedButNothingIsBlocked()
    {
        var status = AiBudgetStatusBuilder.Build(Budget(50m, stop: false), 60m, Now);

        Assert.True(status.LimitReached);
        Assert.False(status.Blocked);
        Assert.Null(status.Message);
    }

    [Fact]
    public void TheMessageSaysWhatIsAndIsNotAffectedAndWhenItResets()
    {
        var status = AiBudgetStatusBuilder.Build(Budget(50m), 50m, Now);

        Assert.Contains("AI budget reached", status.Message);
        Assert.Contains("$50.00 of $50.00", status.Message);
        Assert.Contains("December 1", status.Message);
        Assert.Contains("Patching and Check for Updates are not affected", status.Message);
    }

    [Fact]
    public void ABudgetTypedInAustralianDollarsIsComparedInUsAndShownInAustralian()
    {
        var settings = Budget(155m, "AUD", rate: 1.55m);

        Assert.Equal(100m, settings.MonthlyBudgetUsd);
        var status = AiBudgetStatusBuilder.Build(settings, 100m, Now);
        Assert.True(status.Blocked);
        Assert.Contains("A$155.00 of A$155.00", status.Message);
    }

    [Fact]
    public void TheMonthStartsOnTheFirstInUtcWhateverTheTimeOfDay()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), AiBudgetStatusBuilder.MonthStart(Now));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), AiBudgetStatusBuilder.MonthStart(new DateTimeOffset(2026, 11, 30, 23, 59, 59, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(0, "USD")]
    [InlineData(-5, "USD")]
    [InlineData(0.004, "USD")]
    [InlineData(0.009, "AUD")]
    [InlineData(10.005, "USD")]
    public void ABudgetMustBeAtLeastACentInWholeCentsOrEmpty(double amount, string currency) =>
        Assert.Throws<DomainException>(() => AiBudgetSettings.CreateDefault().Update((decimal)amount, currency, 1.55m, 80, true));

    [Theory]
    [InlineData(0.01)]
    [InlineData(12.5)]
    [InlineData(99.99)]
    public void AnAmountTheColumnStoresExactlyIsAccepted(double amount)
    {
        var settings = AiBudgetSettings.CreateDefault();
        settings.Update((decimal)amount, "USD", 1.55m, 80, true);

        Assert.Equal((decimal)amount, settings.BudgetAmount);
    }

    [Fact]
    public void AZeroLimitSavedBeforeSubCentAmountsWereRefusedReadsAsReachedRatherThanThrowing()
    {
        // What a 0.004 budget became in a numeric(18,2) column. It used to divide by zero inside
        // every AI call's budget check.
        var settings = Budget(1m);
        typeof(AiBudgetSettings).GetProperty(nameof(AiBudgetSettings.BudgetAmount))!.SetValue(settings, 0m);

        var status = AiBudgetStatusBuilder.Build(settings, 0m, Now);

        Assert.True(status.LimitReached);
        Assert.True(status.Blocked);
        Assert.Equal(100, status.Percent);
    }

    [Fact]
    public void UnpricedCallsCountAgainstTheLimitAtTheFallbackRateAndAreReportedAsSuch()
    {
        // 1M in and 100K out on a model nobody prices: $15 + $7.50 at the fallback.
        var spend = new AiSpendTotals(PricedUsd: 0m, UnpricedCalls: 3, UnpricedInputTokens: 1_000_000, UnpricedOutputTokens: 100_000);

        var status = AiBudgetStatusBuilder.Build(Budget(20m), spend, Now);

        Assert.Equal(22.5m, status.SpentUsd);
        Assert.Equal(22.5m, status.UnpricedEstimateUsd);
        Assert.Equal(3, status.UnpricedCalls);
        Assert.True(status.Blocked, "an unpriced model must not be a way around the cap");
    }

    [Fact]
    public void TheInMemoryTotalsCountFreeCallsAsFreeAndOnlyNullCostsAsUnpriced()
    {
        var spend = AiSpendTotals.From(
        [
            Record(AiFeature.ScriptResearch, "A", "claude-sonnet-5-5", null, 1_000_000, 0, 2m),
            Record(AiFeature.CpeSuggestion, "Local", "llama3", null, 1_000_000, 1_000_000, 0m),
            Record(AiFeature.ScriptRepair, "B", "mystery-model", null, 200_000, 20_000, null),
        ]);

        Assert.Equal(2m, spend.PricedUsd);
        Assert.Equal(1, spend.UnpricedCalls);
        Assert.Equal((200_000L, 20_000L), (spend.UnpricedInputTokens, spend.UnpricedOutputTokens));
        Assert.Equal(2m + 3m + 1.5m, spend.TotalUsd);
    }

    [Fact]
    public void TheCurrencyRateAndWarnLevelAreValidated()
    {
        var settings = AiBudgetSettings.CreateDefault();

        Assert.Throws<DomainException>(() => settings.Update(10m, "EUR", 1.55m, 80, true));
        Assert.Throws<DomainException>(() => settings.Update(10m, "AUD", 0m, 80, true));
        Assert.Throws<DomainException>(() => settings.Update(10m, "USD", 1.55m, 0, true));
        Assert.Throws<DomainException>(() => settings.Update(10m, "USD", 1.55m, 100, true));
        settings.Update(null, "usd", 1.55m, 80, true);
        Assert.Null(settings.MonthlyBudgetUsd);
        Assert.Equal("USD", settings.BudgetCurrency);
    }

    [Fact]
    public void ByDefaultTheStopIsOnAndTheWarningIsEightyPercent()
    {
        var settings = AiBudgetSettings.CreateDefault();

        Assert.True(settings.StopAtLimit);
        Assert.Equal(80, settings.WarnPercent);
    }

    // --- Pricing ---------------------------------------------------------------------------

    private static AiCatalogDto Catalog(params (string Provider, string Model, decimal? In, decimal? Out)[] models) =>
        new(Now, false, models.GroupBy(m => m.Provider).Select(g => new AiCatalogProviderDto(
            g.Key, g.Key, AiWireProtocol.Anthropic, null, true, false, null,
            g.Select(m => new AiCatalogModelDto(m.Model, m.Model, AiWireProtocol.Anthropic, true, false, null, null, m.In, m.Out)).ToList())).ToList());

    [Fact]
    public void ACostIsTheTokensTimesThePerMillionPrices()
    {
        var catalog = Catalog(("anthropic", "claude-sonnet-5-5", 2m, 10m));

        // 1.9M in at $2 and 310K out at $10 per million.
        Assert.Equal(6.9m, AiPricing.Estimate(catalog, "claude-sonnet-5-5", 1_900_000, 310_000));
    }

    [Fact]
    public void AVertexVersionSuffixOnEitherSideStillMatches()
    {
        var catalog = Catalog(("google-vertex", "claude-sonnet-5-5@default", 2m, 10m));

        Assert.Equal(12m, AiPricing.Estimate(catalog, "claude-sonnet-5-5", 1_000_000, 1_000_000));
        Assert.Equal(12m, AiPricing.Estimate(Catalog(("anthropic", "claude-sonnet-5-5", 2m, 10m)), "claude-sonnet-5-5@default", 1_000_000, 1_000_000));
    }

    [Fact]
    public void AModelWithNoPriceOrNoEntryIsUnknownNotFree()
    {
        var catalog = Catalog(("openai", "gpt-x", null, null));

        Assert.Null(AiPricing.Estimate(catalog, "gpt-x", 1000, 1000));
        Assert.Null(AiPricing.Estimate(catalog, "never-heard-of-it", 1000, 1000));
        Assert.Null(AiPricing.Estimate(new AiCatalogDto(null, true, []), "claude-sonnet-5-5", 1000, 1000));
    }

    // --- Scope -----------------------------------------------------------------------------

    [Fact]
    public void AScopeCollectsWhatIsAddedInsideItAndNothingOutside()
    {
        AiUsageScope.Add(new AiUsageEntry("c", "m", 1, 1));

        using var scope = AiUsageScope.Begin();
        AiUsageScope.Add(new AiUsageEntry("c", "m", 10, 5));
        AiUsageScope.Add(new AiUsageEntry("c", "m", 20, 6));

        Assert.Equal(2, scope.Entries.Count);
        Assert.Equal(30, scope.Entries.Sum(e => e.InputTokens));
    }

    [Fact]
    public void ANestedScopeKeepsItsOwnAndGivesTheOuterOneBackWhenItEnds()
    {
        using var outer = AiUsageScope.Begin();
        using (var inner = AiUsageScope.Begin())
        {
            AiUsageScope.Add(new AiUsageEntry("c", "m", 1, 1));
            Assert.Single(inner.Entries);
        }

        AiUsageScope.Add(new AiUsageEntry("c", "m", 2, 2));

        Assert.Single(outer.Entries);
        Assert.Equal(2, outer.Entries[0].InputTokens);
    }

    [Fact]
    public async Task AScopeFollowsTheCallThroughAwaits()
    {
        using var scope = AiUsageScope.Begin();

        await Task.Run(async () =>
        {
            await Task.Delay(1);
            AiUsageScope.Add(new AiUsageEntry("c", "m", 3, 4));
        });

        Assert.Single(scope.Entries);
    }

    // --- The usage screen's numbers ---------------------------------------------------------

    [Fact]
    public void TheBreakdownsAddUpTheMonthAndNameTheMostExpensiveFirst()
    {
        var month = new List<AiUsageRecord>
        {
            Record(AiFeature.ScriptResearch, "Anthropic", "claude-sonnet-5-5", "Teams", 1_000_000, 100_000, 3m),
            Record(AiFeature.ScriptResearch, "Anthropic", "claude-sonnet-5-5", "Teams", 500_000, 50_000, 1.5m),
            Record(AiFeature.ScriptRepair, "Anthropic", "claude-sonnet-5-5", "Slack", 200_000, 20_000, 0.6m),
            Record(AiFeature.CpeSuggestion, "Local", "llama3", null, 100_000, 10_000, 0m),
            Record(AiFeature.ScriptResearch, "Other", "mystery-model", "Zoom", 10_000, 1_000, null),
        };

        var dto = GetAiUsageQueryHandler.Build(Budget(50m), month, month, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), Now.AddDays(-29), Now);

        // The priced calls' 5.1, plus mystery-model's 10K in and 1K out at the $15/$75 fallback.
        Assert.Equal(5.325m, dto.Status.SpentUsd);
        Assert.Equal(1, dto.Status.UnpricedCalls);
        Assert.Equal(0.225m, dto.Status.UnpricedEstimateUsd);
        Assert.Equal(AiFeature.ScriptResearch, dto.ByFeature[0].Feature);
        Assert.Equal(3, dto.ByFeature[0].Calls);
        Assert.Equal(["Teams", "Slack", "Zoom"], dto.BySubject.Select(s => s.Subject));
        Assert.Equal(4.5m, dto.BySubject[0].CostUsd);

        var anthropic = dto.ByModel.Single(m => m.Model == "claude-sonnet-5-5");
        Assert.True(anthropic.PriceKnown);
        Assert.Equal(5.1m, anthropic.CostUsd);
        Assert.False(dto.ByModel.Single(m => m.Model == "mystery-model").PriceKnown, "unknown is not the same as free");
        Assert.True(dto.ByModel.Single(m => m.Model == "llama3").PriceKnown, "a local model's zero is a known price");
        Assert.Equal(month.Sum(r => r.InputTokens + r.OutputTokens), dto.TotalTokens);
    }

    [Fact]
    public void TheChartHasEveryDayEvenTheQuietOnes()
    {
        var chartStart = new DateTimeOffset(Now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-29);
        var window = new List<AiUsageRecord> { Record(AiFeature.ScriptResearch, "A", "m", "x", 100, 50, 1.25m, Now) };

        var dto = GetAiUsageQueryHandler.Build(Budget(50m), window, window, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), chartStart, Now);

        Assert.Equal(30, dto.Days.Count);
        Assert.Equal(DateOnly.FromDateTime(chartStart.UtcDateTime), dto.Days[0].Day);
        Assert.Equal(DateOnly.FromDateTime(Now.UtcDateTime), dto.Days[^1].Day);
        Assert.Equal(1.25m, dto.Days[^1].CostUsd);
        Assert.Equal(0m, dto.Days[0].CostUsd);
    }
}
