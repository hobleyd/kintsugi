using MediatR;

namespace Kintsugi.Application.AiSettings.Commands.UpdateAiBudget;

/// <param name="BudgetAmount">The monthly limit in <paramref name="BudgetCurrency"/>; null for no limit.</param>
public record UpdateAiBudgetCommand(
    decimal? BudgetAmount,
    string BudgetCurrency,
    decimal UsdToAudRate,
    int WarnPercent,
    bool StopAtLimit) : IRequest<AiBudgetSettingsDto>;
