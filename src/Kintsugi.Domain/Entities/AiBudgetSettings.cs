using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// The monthly limit on what the AI may spend, and what to do when it is reached. One row.
/// </summary>
/// <remarks>
/// <para>
/// **The budget is kept in the currency it was typed in, with the exchange rate it needs.** Prices
/// are listed in US dollars (models.dev), so every comparison is made in dollars:
/// <see cref="MonthlyBudgetUsd"/>. The AUD rate is set by an administrator and never fetched, so a
/// limit does not move because an outside rate service did, or was down.
/// </para>
/// <para>
/// **What the stop covers.** At the limit, with <see cref="StopAtLimit"/> on, new AI calls are
/// refused: research, script repair and CPE suggestions. Patching is untouched, because no patch
/// cycle calls the AI — agents run scripts that are already signed.
/// </para>
/// </remarks>
public class AiBudgetSettings : BaseEntity
{
    public const string Usd = "USD";
    public const string Aud = "AUD";
    public const int DefaultWarnPercent = 80;
    public const decimal DefaultUsdToAudRate = 1.55m;

    /// <summary>
    /// How many decimal places a budget amount may carry — the scale of its column
    /// (<c>AiBudgetSettingsConfiguration</c>, <c>HasPrecision(18, 2)</c>) — and so the smallest
    /// limit, one cent.
    /// </summary>
    /// <remarks>
    /// Refused here and in <c>UpdateAiBudgetCommandValidator</c>, rather than rounded, because the
    /// column rounds silently: 0.004 was accepted by the "more than zero" rule, stored as 0.00, and
    /// every AI call's budget check then divided by that zero. The admin UI's AI Usage screen checks
    /// the same two rules before sending. Change all three with the column, never one alone.
    /// </remarks>
    public const int BudgetDecimalPlaces = 2;

    public const decimal MinimumBudgetAmount = 0.01m;

    /// <summary>Whether <paramref name="amount"/> survives the column unchanged and is at least one cent.</summary>
    public static bool IsStorableBudget(decimal amount) =>
        amount >= MinimumBudgetAmount && decimal.Round(amount, BudgetDecimalPlaces) == amount;

    /// <summary>The amount as typed, in <see cref="BudgetCurrency"/>; null means no limit.</summary>
    public decimal? BudgetAmount { get; private set; }

    public string BudgetCurrency { get; private set; } = Usd;

    /// <summary>How many Australian dollars one US dollar is worth, as set by an administrator.</summary>
    public decimal UsdToAudRate { get; private set; } = DefaultUsdToAudRate;

    public int WarnPercent { get; private set; } = DefaultWarnPercent;

    /// <summary>On by default: a runaway scan is the risk a limit exists for.</summary>
    public bool StopAtLimit { get; private set; } = true;

    private AiBudgetSettings()
    {
    }

    public static AiBudgetSettings CreateDefault() => new();

    /// <summary>The limit in US dollars, or null when there is none.</summary>
    public decimal? MonthlyBudgetUsd =>
        BudgetAmount is null ? null : BudgetCurrency == Aud ? BudgetAmount.Value / UsdToAudRate : BudgetAmount.Value;

    public void Update(decimal? budgetAmount, string budgetCurrency, decimal usdToAudRate, int warnPercent, bool stopAtLimit)
    {
        var currency = (budgetCurrency ?? string.Empty).Trim().ToUpperInvariant();
        if (currency is not (Usd or Aud))
        {
            throw new DomainException("The budget currency must be USD or AUD.");
        }

        if (budgetAmount is { } amount && !IsStorableBudget(amount))
        {
            throw new DomainException("The monthly budget must be at least 0.01 with no more than two decimal places, or empty for no limit.");
        }

        if (usdToAudRate <= 0)
        {
            throw new DomainException("The exchange rate must be more than zero.");
        }

        if (warnPercent is < 1 or > 99)
        {
            throw new DomainException("The warning level must be between 1 and 99 percent.");
        }

        BudgetAmount = budgetAmount;
        BudgetCurrency = currency;
        UsdToAudRate = usdToAudRate;
        WarnPercent = warnPercent;
        StopAtLimit = stopAtLimit;
        MarkUpdated();
    }
}
