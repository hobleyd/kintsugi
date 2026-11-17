using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// What one host last reported for one item of one signed <see cref="BaselineVersion"/>.
/// </summary>
/// <remarks>
/// <strong>An agent reports only what changed</strong>, against the states the server handed it in
/// <c>GET /api/baseline</c>, so a compliant fleet sends almost nothing. Every report still touches
/// <see cref="LastCheckedUtc"/> on all of that host's rows for the version, which is what keeps "last
/// checked" true for items nobody needed to resend.
/// </remarks>
public class HostBaselineItemState : BaseEntity
{
    public const int MaxObservedLength = 2048;
    public const int MaxDetailLength = 500;

    public Guid HostId { get; private set; }

    public Guid BaselineVersionId { get; private set; }

    public string ItemKey { get; private set; } = default!;

    public BaselineItemState State { get; private set; }

    /// <summary>The value the agent read, as text (see the agent's <c>baseline::format_value</c>), or
    /// null when the value or key is absent, or the item was not read at all.</summary>
    public string? ObservedValue { get; private set; }

    /// <summary>Why an item is <see cref="BaselineItemState.NotApplicable"/> or in
    /// <see cref="BaselineItemState.Error"/>, in the agent's words.</summary>
    public string? Detail { get; private set; }

    public DateTimeOffset LastCheckedUtc { get; private set; }

    /// <summary>When <see cref="State"/> or <see cref="ObservedValue"/> last changed — drift's own timestamp.</summary>
    public DateTimeOffset LastChangedUtc { get; private set; }

    private HostBaselineItemState()
    {
    }

    public static HostBaselineItemState Create(Guid hostId, Guid baselineVersionId, string itemKey, DateTimeOffset at)
    {
        if (hostId == Guid.Empty || baselineVersionId == Guid.Empty || string.IsNullOrWhiteSpace(itemKey))
        {
            throw new DomainException("An item state must name its host, baseline version and item.");
        }

        return new HostBaselineItemState
        {
            HostId = hostId,
            BaselineVersionId = baselineVersionId,
            ItemKey = itemKey,
            LastCheckedUtc = at,
            LastChangedUtc = at
        };
    }

    public void Record(BaselineItemState state, string? observedValue, string? detail, DateTimeOffset at)
    {
        if (state != State || !string.Equals(observedValue, ObservedValue, StringComparison.Ordinal))
        {
            LastChangedUtc = at;
        }

        State = state;
        ObservedValue = observedValue;
        Detail = detail;
        LastCheckedUtc = at;
        MarkUpdated();
    }

    public void MarkChecked(DateTimeOffset at) => LastCheckedUtc = at;
}
