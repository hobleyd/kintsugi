using Kintsugi.Domain.Common;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// One KPI's value on one day. Recorded daily from the day the Autonomy page exists, because history
/// before the first snapshot cannot be rebuilt and the "prior" column and the trend lines need it.
/// </summary>
public class KpiSnapshot : BaseEntity
{
    /// <summary>Midnight UTC of the day it describes.</summary>
    public DateTimeOffset SnapshotDateUtc { get; private set; }

    /// <summary>The KPI's fixed id, for example <c>failures.open_failure_devices</c>.</summary>
    public string KpiId { get; private set; } = default!;

    public double Value { get; private set; }

    public int Numerator { get; private set; }

    public int Denominator { get; private set; }

    private KpiSnapshot()
    {
    }

    public static KpiSnapshot Record(DateTimeOffset dateUtc, string kpiId, double value, int numerator, int denominator) =>
        new()
        {
            SnapshotDateUtc = new DateTimeOffset(dateUtc.UtcDateTime.Date, TimeSpan.Zero),
            KpiId = kpiId,
            Value = value,
            Numerator = numerator,
            Denominator = denominator,
        };
}
