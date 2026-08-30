using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Domain;

public class PatchingPolicySettingsTests
{
    [Fact]
    public void Create_SetsAllFields()
    {
        var settings = PatchingPolicySettings.Create(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3);

        Assert.Equal(7, settings.IntervalValue);
        Assert.Equal(PatchingTimeUnit.Days, settings.IntervalUnit);
        Assert.Equal(1, settings.DelayValue);
        Assert.Equal(PatchingTimeUnit.Days, settings.DelayUnit);
        Assert.Equal(3, settings.MaxDelayCount);
    }

    [Fact]
    public void Create_RejectsAnIntervalValueBelowOne()
    {
        Assert.Throws<DomainException>(() => PatchingPolicySettings.Create(0, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 0));
    }

    [Fact]
    public void Create_RejectsADelayValueBelowOne()
    {
        Assert.Throws<DomainException>(() => PatchingPolicySettings.Create(7, PatchingTimeUnit.Days, 0, PatchingTimeUnit.Days, 0));
    }

    [Fact]
    public void Create_RejectsANegativeMaxDelayCount()
    {
        Assert.Throws<DomainException>(() => PatchingPolicySettings.Create(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, -1));
    }

    [Fact]
    public void Create_AllowsAZeroMaxDelayCount_MeaningNoDeferralPermitted()
    {
        var settings = PatchingPolicySettings.Create(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 0);

        Assert.Equal(0, settings.MaxDelayCount);
    }

    [Fact]
    public void Update_ReplacesAllFields()
    {
        var settings = PatchingPolicySettings.Create(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3);

        settings.Update(24, PatchingTimeUnit.Hours, 4, PatchingTimeUnit.Hours, 5);

        Assert.Equal(24, settings.IntervalValue);
        Assert.Equal(PatchingTimeUnit.Hours, settings.IntervalUnit);
        Assert.Equal(4, settings.DelayValue);
        Assert.Equal(PatchingTimeUnit.Hours, settings.DelayUnit);
        Assert.Equal(5, settings.MaxDelayCount);
    }

    [Fact]
    public void Update_RejectsAnIntervalValueBelowOne_LeavingExistingValuesUnchanged()
    {
        var settings = PatchingPolicySettings.Create(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3);

        Assert.Throws<DomainException>(() => settings.Update(0, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3));
        Assert.Equal(7, settings.IntervalValue);
    }
}
