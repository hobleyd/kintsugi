using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Autonomy;

/// <summary>The three fields of a host event that the Autonomy page needs to tell who started a
/// patch, read without the rest of the row (no output, no actor).</summary>
public record PatchActivityRecord(Guid HostId, HostEventKind Kind, string Subject, DateTimeOffset OccurredUtc);
