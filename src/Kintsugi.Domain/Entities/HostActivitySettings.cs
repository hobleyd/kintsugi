using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>Singleton: how long host activity events are kept.</summary>
public class HostActivitySettings : BaseEntity
{
    public const int DefaultRetentionMonths = 6;
    public const int MaxRetentionMonths = 60;

    public int RetentionMonths { get; private set; } = DefaultRetentionMonths;

    private HostActivitySettings()
    {
    }

    public static HostActivitySettings Create(int retentionMonths)
    {
        var settings = new HostActivitySettings();
        settings.Apply(retentionMonths);
        return settings;
    }

    public void Update(int retentionMonths)
    {
        Apply(retentionMonths);
        MarkUpdated();
    }

    private void Apply(int retentionMonths)
    {
        if (retentionMonths is < 1 or > MaxRetentionMonths)
        {
            throw new DomainException($"Retention must be between 1 and {MaxRetentionMonths} months.");
        }

        RetentionMonths = retentionMonths;
    }
}
