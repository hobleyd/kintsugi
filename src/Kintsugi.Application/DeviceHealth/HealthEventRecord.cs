using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.DeviceHealth;

/// <summary>The two fields of a health event the assessment needs, read without the rest of the row.</summary>
public record HealthEventRecord(Guid HostId, HostEventKind Kind, DateTimeOffset OccurredUtc);
