using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>The AI usage ledger and the budget settings beside it.</summary>
public interface IAiUsageRepository
{
    Task<AiBudgetSettings?> GetBudgetAsync(CancellationToken cancellationToken);

    Task AddBudgetAsync(AiBudgetSettings settings, CancellationToken cancellationToken);

    Task AddUsageAsync(AiUsageRecord record, CancellationToken cancellationToken);

    /// <summary>Everything recorded at or after <paramref name="sinceUtc"/>, oldest first.</summary>
    Task<IReadOnlyList<AiUsageRecord>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    /// <summary>What was spent at or after <paramref name="sinceUtc"/>: the recorded costs, and the
    /// calls with no known price with their tokens, which the budget charges at
    /// <see cref="AiSettings.AiPricing.FallbackInputPerMillion"/> rather than as nothing.</summary>
    Task<AiSettings.AiSpendTotals> GetSpendSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);
}
