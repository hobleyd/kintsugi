using Kintsugi.Application.AiSettings;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Asks the configured AI provider to propose a CPE vendor and product for an application whose
/// name is all this system knows about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The model proposes a search term; it never asserts a fact.</b> Whatever comes back is passed
/// to <see cref="INvdClient.CpeExistsAsync"/> and discarded unless NVD's own dictionary contains
/// that vendor and product, and a human still confirms it before anything is assessed. That is
/// what separates this from the thing this codebase refuses elsewhere: <c>VantaResourceBuilder</c>
/// will not let a model near a severity number, because a severity has no external authority to
/// check it against, whereas "does <c>a:mozilla:firefox</c> name a real product" has exactly one.
/// Verified against the live dictionary: <c>a:mozilla:firefox</c> answers 1199 entries, the
/// invented <c>a:mozilla:firefax</c> answers 0, and so does the plausible-but-wrong
/// <c>a:slack:slack</c>.
/// </para>
/// <para>
/// Implemented on <c>AiUpgradePathResearchClient</c> rather than as its own class, so it reuses
/// that type's per-provider dispatch (Anthropic, OpenAI, Ollama, Goose, the Claude Agent SDK)
/// instead of growing a second, divergent copy of it. It is a separate interface because it is a
/// separate concern — nothing about upgrade scripts is involved.
/// </para>
/// </remarks>
public interface ICpeSuggestionClient
{
    /// <summary>
    /// Proposes a vendor and product for <paramref name="displayName"/>, or null if the model
    /// declines — which is the right answer for an in-house tool NVD has never heard of, and is
    /// treated as such rather than as a failure. Throws only if the provider call itself failed.
    /// </summary>
    /// <param name="part">The CPE part letter the answer must be for: <c>a</c> for an application,
    /// <c>o</c> for an operating system. Passed in rather than left to the model, because it is
    /// known for certain and a model that picks the other one produces a suggestion that fails the
    /// dictionary check for a reason nobody can see.</param>
    Task<CpeSuggestion?> SuggestCpeAsync(
        AiProviderSettings settings, string displayName, string part, CancellationToken cancellationToken);
}

/// <param name="Notes">The model's one-line reasoning, shown beside the Confirm button so a
/// reviewer knows what they are being asked to accept.</param>
public record CpeSuggestion(string Vendor, string Product, string? Notes);
