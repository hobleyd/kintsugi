using Kintsugi.Application.AiSettings;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Records what AI calls cost and refuses new ones once the monthly limit is reached.
/// </summary>
/// <remarks>
/// A singleton that opens its own scope for each call, because its callers (the research client,
/// the scan coordinator) are not themselves request-scoped.
/// </remarks>
public interface IAiBudgetService
{
    /// <summary>Throws <see cref="Common.Exceptions.AiBudgetReachedException"/> when a limit is set,
    /// the stop is on, and this month's estimated spend has reached it.</summary>
    Task EnsureWithinBudgetAsync(CancellationToken cancellationToken);

    /// <summary>Writes one ledger row per entry. Never throws: failing to record a cost must not
    /// fail the research it was recording.</summary>
    Task RecordAsync(AiFeature feature, string? subject, IReadOnlyList<AiUsageEntry> entries, CancellationToken cancellationToken);

    Task<AiBudgetStatusDto> GetStatusAsync(CancellationToken cancellationToken);
}
