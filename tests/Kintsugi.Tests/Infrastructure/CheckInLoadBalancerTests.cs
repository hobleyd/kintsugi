using Kintsugi.Infrastructure.CheckIn;

namespace Kintsugi.Tests.Infrastructure;

public class CheckInLoadBalancerTests
{
    [Fact]
    public void RecordCheckIn_FirstEverCheckIn_DoesNotReassign()
    {
        var balancer = new CheckInLoadBalancer();

        var suggestion = balancer.RecordCheckIn("HOST-1", 10);

        Assert.Null(suggestion);
    }

    [Fact]
    public void RecordCheckIn_SameHostRetryingOnTheSameMinuteManyTimes_NeverReassignsItself()
    {
        var balancer = new CheckInLoadBalancer();

        // A host stuck retrying (e.g. a chain of self-update restarts) reporting the same minute
        // over and over must never look like "lots of load on this minute" on its own — that was
        // a real bug: a single host could inflate its own bucket past the threshold and get
        // bounced from minute to minute indefinitely.
        int? suggestion = null;
        for (var i = 0; i < 20; i++)
        {
            suggestion = balancer.RecordCheckIn("HOST-1", 40);
        }

        Assert.Null(suggestion);
    }

    [Fact]
    public void RecordCheckIn_SmallGapBetweenMinutes_DoesNotReassign()
    {
        var balancer = new CheckInLoadBalancer();

        balancer.RecordCheckIn("HOST-1", 5);
        var suggestion = balancer.RecordCheckIn("HOST-2", 5);

        // Two distinct hosts on the same minute vs. zero everywhere else is normal noise, not the
        // kind of imbalance worth an agent rewriting its own LaunchDaemon plist over.
        Assert.Null(suggestion);
    }

    [Fact]
    public void RecordCheckIn_WhenOneMinuteIsClearlyOverloaded_ReassignsToTheOnlyLeastLoadedMinute()
    {
        var balancer = new CheckInLoadBalancer();

        // Give every minute except 0 and 59 a single distinct host, so once minute 0 gets three
        // distinct hosts, minute 59 is unambiguously the sole least-loaded minute to reassign to.
        for (var minute = 1; minute < 59; minute++)
        {
            balancer.RecordCheckIn($"HOST-{minute}", minute);
        }

        balancer.RecordCheckIn("HOST-A", 0);
        balancer.RecordCheckIn("HOST-B", 0);
        var suggestion = balancer.RecordCheckIn("HOST-C", 0);

        Assert.Equal(59, suggestion);
    }

    [Fact]
    public void RecordCheckIn_NeverSuggestsTheOverloadedMinuteItself()
    {
        var balancer = new CheckInLoadBalancer();

        int? suggestion = null;
        for (var i = 0; i < 10; i++)
        {
            suggestion = balancer.RecordCheckIn($"HOST-{i}", 30);
        }

        Assert.NotNull(suggestion);
        Assert.NotEqual(30, suggestion);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60)]
    public void RecordCheckIn_MinuteOutOfRange_Throws(int minute)
    {
        var balancer = new CheckInLoadBalancer();

        Assert.Throws<ArgumentOutOfRangeException>(() => balancer.RecordCheckIn("HOST-1", minute));
    }
}
