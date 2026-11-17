using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// A read-only value one host reported because a baseline asked for it — Windows edition, whether
/// hibernation is on. Keyed by the fact item's key, per host rather than per baseline version, because
/// a fact is about the machine (and is what targeting by rule will read in a later step).
/// </summary>
public class HostFact : BaseEntity
{
    public const int MaxValueLength = HostBaselineItemState.MaxObservedLength;

    public Guid HostId { get; private set; }

    public string Key { get; private set; } = default!;

    /// <summary>Null when the value is absent on the device.</summary>
    public string? Value { get; private set; }

    public DateTimeOffset LastCheckedUtc { get; private set; }

    public DateTimeOffset LastChangedUtc { get; private set; }

    private HostFact()
    {
    }

    public static HostFact Create(Guid hostId, string key, string? value, DateTimeOffset at)
    {
        if (hostId == Guid.Empty || string.IsNullOrWhiteSpace(key))
        {
            throw new DomainException("A fact must name its host and key.");
        }

        return new HostFact { HostId = hostId, Key = key, Value = value, LastCheckedUtc = at, LastChangedUtc = at };
    }

    public void Record(string? value, DateTimeOffset at)
    {
        if (!string.Equals(value, Value, StringComparison.Ordinal))
        {
            Value = value;
            LastChangedUtc = at;
            MarkUpdated();
        }

        LastCheckedUtc = at;
    }

    public void MarkChecked(DateTimeOffset at) => LastCheckedUtc = at;
}
