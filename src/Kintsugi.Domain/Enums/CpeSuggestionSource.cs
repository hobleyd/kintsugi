namespace Kintsugi.Domain.Enums;

/// <summary>
/// Where a <see cref="Entities.CpeMapping"/>'s proposed vendor and product came from, shown beside
/// it on the mapping queue so a reviewer knows how much to trust what they are being asked to
/// confirm.
/// </summary>
/// <remarks>
/// Ordinal on the wire, like <see cref="CpeMappingStatus"/>; append only. The mirror is
/// <c>CpeSuggestionSource</c> in <c>web/lib/domain/entities/enums.dart</c>.
/// </remarks>
public enum CpeSuggestionSource
{
    /// <summary>Nothing has been proposed.</summary>
    None,

    /// <summary>Proposed by the configured AI provider and then confirmed to exist in NVD's CPE
    /// dictionary. The model chose a search term; it did not assert a fact.</summary>
    Ai,

    /// <summary>Picked out of NVD's CPE dictionary by keyword search — either by a human on the
    /// mapping screen, or automatically where the search returned exactly one candidate vendor and
    /// product. Note that a keyword search is a weak signal on its own: "slack" ranks Slackware
    /// Linux first and "zoom" ranks ZoomText, which is why even this source still needs a human.</summary>
    Dictionary,

    /// <summary>Typed in by a human, who is then confirming their own entry.</summary>
    Manual
}
