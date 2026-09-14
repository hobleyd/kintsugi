using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Domain;

public class ForcedPatchRunTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static ForcedPatchRun Run(DateTimeOffset? requestedUtc = null) =>
        ForcedPatchRun.Request(Guid.NewGuid(), "Firefox", "macOS", requestedUtc ?? Noon, ForcedPatchRun.DefaultLifetime);

    [Fact]
    public void Request_RefusesARunThatNamesNoHost() =>
        Assert.Throws<DomainException>(() =>
            ForcedPatchRun.Request(Guid.Empty, "Firefox", "macOS", Noon, ForcedPatchRun.DefaultLifetime));

    [Fact]
    public void Request_RefusesARunThatNamesNoApplication() =>
        Assert.Throws<DomainException>(() =>
            ForcedPatchRun.Request(Guid.NewGuid(), "  ", "macOS", Noon, ForcedPatchRun.DefaultLifetime));

    /// <summary>
    /// The expiry is the whole reason a laptop shut in a bag for three weeks does not come back and
    /// start a five-minute countdown for an emergency that was over a fortnight ago.
    /// </summary>
    [Fact]
    public void IsCollectableAt_StopsBeingTrueOnceTheRunHasExpired()
    {
        var run = Run();

        Assert.True(run.IsCollectableAt(Noon));
        Assert.True(run.IsCollectableAt(Noon + ForcedPatchRun.DefaultLifetime - TimeSpan.FromMinutes(1)));
        Assert.False(run.IsCollectableAt(Noon + ForcedPatchRun.DefaultLifetime));
    }

    [Fact]
    public void IsCollectableAt_StopsBeingTrueOnceAnAgentHasTakenIt()
    {
        var run = Run();
        run.MarkCollected(Noon);

        Assert.False(run.IsCollectableAt(Noon));
    }

    /// <summary>
    /// A retried request that arrives twice must not move the timestamp — it is the record of when
    /// the host was actually told.
    /// </summary>
    [Fact]
    public void MarkCollected_KeepsTheFirstTimestamp()
    {
        var run = Run();
        run.MarkCollected(Noon);
        run.MarkCollected(Noon.AddMinutes(5));

        Assert.Equal(Noon, run.CollectedUtc);
    }

    [Fact]
    public void Renew_MovesTheExpiryForward()
    {
        var run = Run();
        run.Renew(Noon.AddHours(20), ForcedPatchRun.DefaultLifetime);

        Assert.Equal(Noon.AddHours(20) + ForcedPatchRun.DefaultLifetime, run.ExpiresUtc);
    }

    /// <summary>
    /// Renewing a collected row would resurrect an instruction the agent is already carrying out,
    /// and the agent would patch the same application twice.
    /// </summary>
    [Fact]
    public void Renew_RefusesARunAnAgentHasAlreadyCollected()
    {
        var run = Run();
        run.MarkCollected(Noon);

        Assert.Throws<DomainException>(() => run.Renew(Noon.AddHours(1), ForcedPatchRun.DefaultLifetime));
    }
}
