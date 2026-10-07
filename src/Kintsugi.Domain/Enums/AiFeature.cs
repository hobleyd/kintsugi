namespace Kintsugi.Domain.Enums;

/// <summary>
/// The jobs the AI does here, each of which can be routed to its own connection and model. They
/// differ enough to be worth separating: research needs a strong model with web access, while a
/// self-correction pass over shellcheck findings or a CPE lookup-key guess is a cheap,
/// search-free call.
/// </summary>
/// <remarks>Persisted by name. Mirrored in <c>web/lib/domain/entities/enums.dart</c>.</remarks>
public enum AiFeature
{
    /// <summary>Researching an application and writing its upgrade script — the one feature that
    /// gets web search. Required: Routed mode is not configured without it.</summary>
    ScriptResearch,

    /// <summary>The one self-correction pass over a script that failed validation. Falls back to
    /// <see cref="ScriptResearch"/>'s route when unset.</summary>
    ScriptRepair,

    /// <summary>Proposing a CPE vendor and product for the vulnerability mapping queue. Falls back
    /// to <see cref="ScriptRepair"/>, then <see cref="ScriptResearch"/>.</summary>
    CpeSuggestion,
}
