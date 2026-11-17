using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Infrastructure.Ai;
using Kintsugi.Infrastructure.Persistence;
using Kintsugi.Infrastructure.Persistence.Repositories;

namespace Kintsugi.Tests.Infrastructure;

/// <summary>
/// The budget service against a real (in-memory) database: what it writes for a call, what it
/// reads back as this month's spend, and when it refuses.
/// </summary>
public class AiBudgetServiceTests
{
    private static (AiBudgetService Service, ServiceProvider Provider) Create(AiCatalogDto catalog)
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<IAiUsageRepository, AiUsageRepository>();
        services.AddScoped(_ => Mock.Of<IAiModelCatalog>(c => c.GetAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()) == Task.FromResult(catalog)));
        var provider = services.BuildServiceProvider();
        return (new AiBudgetService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AiBudgetService>.Instance), provider);
    }

    private static AiCatalogDto SonnetPriced() =>
        new(DateTimeOffset.UtcNow, false,
        [
            new AiCatalogProviderDto("anthropic", "Anthropic", AiWireProtocol.Anthropic, null, true, false, null,
            [
                new AiCatalogModelDto("claude-sonnet-5-5", "Sonnet", AiWireProtocol.Anthropic, true, false, null, null, 2m, 10m),
            ]),
        ]);

    private static async Task SetBudget(ServiceProvider provider, decimal? amount, bool stop = true)
    {
        using var scope = provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAiUsageRepository>();
        var settings = AiBudgetSettings.CreateDefault();
        settings.Update(amount, "USD", 1.55m, 80, stop);
        await repository.AddBudgetAsync(settings, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ACallIsRecordedWithTheEstimatedCostAndWhatItWasFor()
    {
        var (service, provider) = Create(SonnetPriced());

        await service.RecordAsync(AiFeature.ScriptResearch, "Slack",
            [new AiUsageEntry("Anthropic", "claude-sonnet-5-5", 1_000_000, 100_000)], CancellationToken.None);

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await context.AiUsage.SingleAsync();
        Assert.Equal("Slack", row.Subject);
        Assert.Equal(AiFeature.ScriptResearch, row.Feature);
        Assert.Equal(3m, row.EstimatedCostUsd);
    }

    [Fact]
    public async Task ALocalModelCostsNothingAndAnUnpricedOneIsUnknown()
    {
        var (service, provider) = Create(SonnetPriced());

        await service.RecordAsync(AiFeature.CpeSuggestion, "Zoom",
            [new AiUsageEntry("Local", "llama3", 5000, 500, Free: true), new AiUsageEntry("Other", "no-such-model", 5000, 500)],
            CancellationToken.None);

        using var scope = provider.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AiUsage.OrderBy(r => r.Model).ToListAsync();
        Assert.Equal(0m, rows.Single(r => r.Model == "llama3").EstimatedCostUsd);
        Assert.Null(rows.Single(r => r.Model == "no-such-model").EstimatedCostUsd);
    }

    [Fact]
    public async Task WithNoBudgetSetNothingIsRefused()
    {
        var (service, _) = Create(SonnetPriced());
        await service.RecordAsync(AiFeature.ScriptResearch, "Slack", [new AiUsageEntry("A", "claude-sonnet-5-5", 100_000_000, 0)], CancellationToken.None);

        await service.EnsureWithinBudgetAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OnceTheMonthsSpendReachesTheLimitNewCallsAreRefusedWithTheReason()
    {
        var (service, provider) = Create(SonnetPriced());
        await SetBudget(provider, 5m);
        // $2 per million input: 2.5M tokens is exactly $5.
        await service.RecordAsync(AiFeature.ScriptResearch, "Slack", [new AiUsageEntry("A", "claude-sonnet-5-5", 2_500_000, 0)], CancellationToken.None);

        var refused = await Assert.ThrowsAsync<AiBudgetReachedException>(() => service.EnsureWithinBudgetAsync(CancellationToken.None));

        Assert.Contains("$5.00 of $5.00", refused.Message);
        Assert.True((await service.GetStatusAsync(CancellationToken.None)).Blocked);
    }

    [Fact]
    public async Task JustUnderTheLimitIsStillAllowed()
    {
        var (service, provider) = Create(SonnetPriced());
        await SetBudget(provider, 5m);
        await service.RecordAsync(AiFeature.ScriptResearch, "Slack", [new AiUsageEntry("A", "claude-sonnet-5-5", 2_000_000, 0)], CancellationToken.None);

        await service.EnsureWithinBudgetAsync(CancellationToken.None);
        Assert.False((await service.GetStatusAsync(CancellationToken.None)).LimitReached);
    }

    [Fact]
    public async Task WithTheStopOffTheLimitDoesNotRefuseAnything()
    {
        var (service, provider) = Create(SonnetPriced());
        await SetBudget(provider, 1m, stop: false);
        await service.RecordAsync(AiFeature.ScriptResearch, "Slack", [new AiUsageEntry("A", "claude-sonnet-5-5", 5_000_000, 0)], CancellationToken.None);

        await service.EnsureWithinBudgetAsync(CancellationToken.None);
        var status = await service.GetStatusAsync(CancellationToken.None);
        Assert.True(status.LimitReached);
        Assert.False(status.Blocked);
    }

    [Fact]
    public async Task AModelWithNoPriceStillCountsTowardTheLimitAtTheFallbackRate()
    {
        var (service, provider) = Create(SonnetPriced());
        await SetBudget(provider, 5m);
        // Unpriced, so the ledger row is null — and the budget charges it at $15 per million input:
        // 400K tokens is $6, over the $5 limit. Counted as nothing, it never would have been.
        await service.RecordAsync(AiFeature.ScriptResearch, "Slack", [new AiUsageEntry("Other", "no-such-model", 400_000, 0)], CancellationToken.None);

        await Assert.ThrowsAsync<AiBudgetReachedException>(() => service.EnsureWithinBudgetAsync(CancellationToken.None));
        var status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(6m, status.SpentUsd);
        Assert.Equal(1, status.UnpricedCalls);
        Assert.Equal(6m, status.UnpricedEstimateUsd);
    }

    [Fact]
    public async Task ALocalModelsZeroIsNotChargedTheFallback()
    {
        var (service, provider) = Create(SonnetPriced());
        await SetBudget(provider, 1m);
        await service.RecordAsync(AiFeature.CpeSuggestion, "Zoom", [new AiUsageEntry("Local", "llama3", 10_000_000, 10_000_000, Free: true)], CancellationToken.None);

        await service.EnsureWithinBudgetAsync(CancellationToken.None);
        Assert.Equal(0, (await service.GetStatusAsync(CancellationToken.None)).UnpricedCalls);
    }

    [Fact]
    public async Task SpendFromLastMonthDoesNotCountAgainstThisOne()
    {
        var (service, provider) = Create(SonnetPriced());
        await SetBudget(provider, 5m);
        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var lastMonth = AiBudgetStatusBuilder.MonthStart(DateTimeOffset.UtcNow).AddDays(-1);
            context.AiUsage.Add(AiUsageRecord.Create(lastMonth, AiFeature.ScriptResearch, "A", "claude-sonnet-5-5", "Slack", 1, 1, 400m));
            await context.SaveChangesAsync();
        }

        await service.EnsureWithinBudgetAsync(CancellationToken.None);
        Assert.Equal(0m, (await service.GetStatusAsync(CancellationToken.None)).SpentUsd);
    }

    [Fact]
    public async Task FailingToRecordNeverFailsTheCaller()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var service = new AiBudgetService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AiBudgetService>.Instance);

        // No repository is registered, so recording throws inside; it must be swallowed.
        await service.RecordAsync(AiFeature.ScriptResearch, "Slack", [new AiUsageEntry("A", "m", 1, 1)], CancellationToken.None);
    }
}
