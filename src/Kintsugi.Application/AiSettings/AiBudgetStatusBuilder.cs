using System.Globalization;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.AiSettings;

/// <summary>Works out where a month's spending stands against the limit, and says so in words.</summary>
public static class AiBudgetStatusBuilder
{
    /// <summary>The first instant of the month <paramref name="now"/> falls in, in UTC. A calendar
    /// month, so "resets on the 1st" means what it says for everyone, whatever their time zone.</summary>
    public static DateTimeOffset MonthStart(DateTimeOffset now) =>
        new(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);

    public static AiBudgetStatusDto Build(AiBudgetSettings settings, decimal spentUsd, DateTimeOffset now) =>
        Build(settings, new AiSpendTotals(spentUsd, 0, 0, 0), now);

    /// <summary>Counts <paramref name="spend"/> as the budget does — unpriced calls at the fallback
    /// rate (see <see cref="AiPricing.FallbackInputPerMillion"/>) — and reports how much of the total
    /// that rate accounts for.</summary>
    public static AiBudgetStatusDto Build(AiBudgetSettings settings, AiSpendTotals spend, DateTimeOffset now)
    {
        var spentUsd = spend.TotalUsd;
        var unpricedUsd = spend.UnpricedEstimateUsd;
        var budget = settings.MonthlyBudgetUsd;
        if (budget is null)
        {
            return new AiBudgetStatusDto(spentUsd, null, 0, false, false, false, null, spend.UnpricedCalls, unpricedUsd);
        }

        // A limit that is zero or less can only come from a row saved before sub-cent amounts were
        // refused (a 0.004 budget rounded to 0.00 by the column) — it used to divide by zero here,
        // inside every AI call's budget check. It is treated as a limit already reached, which is
        // the nearest reading of "a budget of almost nothing", until an administrator saves a real one.
        var percent = budget.Value > 0m ? (int)Math.Floor(spentUsd * 100m / budget.Value) : 100;
        var limitReached = spentUsd >= budget.Value;
        var warnReached = percent >= settings.WarnPercent;
        var blocked = limitReached && settings.StopAtLimit;

        var message = blocked
            ? $"AI budget reached: {Money(spentUsd, settings)} of {Money(budget.Value, settings)} has been spent this month, so no new AI "
              + $"research will be started until {MonthStart(now).AddMonths(1).ToString("MMMM d", CultureInfo.InvariantCulture)} or until an "
              + "administrator raises the budget in Settings → AI Usage. Patching and Check for Updates are not affected."
            : null;

        return new AiBudgetStatusDto(spentUsd, budget, percent, warnReached, limitReached, blocked, message, spend.UnpricedCalls, unpricedUsd);
    }

    private static string Money(decimal usd, AiBudgetSettings settings) =>
        settings.BudgetCurrency == AiBudgetSettings.Aud
            ? "A$" + (usd * settings.UsdToAudRate).ToString("0.00", CultureInfo.InvariantCulture)
            : "$" + usd.ToString("0.00", CultureInfo.InvariantCulture);
}
