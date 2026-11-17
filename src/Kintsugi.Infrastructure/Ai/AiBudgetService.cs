using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Ai;

/// <inheritdoc cref="IAiBudgetService" />
public sealed class AiBudgetService : IAiBudgetService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AiBudgetService> _logger;

    public AiBudgetService(IServiceScopeFactory scopes, ILogger<AiBudgetService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public async Task EnsureWithinBudgetAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (status.Blocked)
        {
            throw new AiBudgetReachedException(status.Message!);
        }
    }

    public async Task<AiBudgetStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAiUsageRepository>();

        var settings = await repository.GetBudgetAsync(cancellationToken) ?? AiBudgetSettings.CreateDefault();
        var now = DateTimeOffset.UtcNow;
        var spent = await repository.GetSpendSinceAsync(AiBudgetStatusBuilder.MonthStart(now), cancellationToken);

        return AiBudgetStatusBuilder.Build(settings, spent, now);
    }

    public async Task RecordAsync(AiFeature feature, string? subject, IReadOnlyList<AiUsageEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return;
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAiUsageRepository>();
            var models = scope.ServiceProvider.GetRequiredService<IAiModelCatalog>();
            var catalog = await models.GetAsync(forceRefresh: false, cancellationToken);
            if (!catalog.Providers.Any(p => p.Models.Any(m => m.InputPerMillion is not null)))
            {
                // A catalogue cached before prices were read has none, and would price nothing for
                // up to a day. Refreshing never throws; at worst it serves the same copy again.
                catalog = await models.GetAsync(forceRefresh: true, cancellationToken);
            }
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var now = DateTimeOffset.UtcNow;

            foreach (var entry in entries)
            {
                var cost = entry.Free ? 0m : AiPricing.Estimate(catalog, entry.Model, entry.InputTokens, entry.OutputTokens);
                await repository.AddUsageAsync(
                    AiUsageRecord.Create(now, feature, entry.ConnectionName, entry.Model, subject, entry.InputTokens, entry.OutputTokens, cost),
                    cancellationToken);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Never fail the research this was recording. The cost of a call that went unrecorded is
            // one call's worth of spend missing from a total that is an estimate anyway.
            _logger.LogWarning(ex, "Could not record the AI usage for {Feature} ({Subject}).", feature, subject);
        }
    }
}
