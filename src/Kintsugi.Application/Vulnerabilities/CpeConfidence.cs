using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Vulnerabilities;

/// <summary>How much weight the mapping queue's own evidence puts behind a vendor and product.
/// Ordinal on the wire; append only — see <c>web/lib/data/models/vulnerability_mapper.dart</c>.</summary>
public enum CpeConfidenceLevel
{
    /// <summary>Nothing is proposed, so there is nothing to be confident about.</summary>
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <param name="Level">What the queue's Confidence column shows.</param>
/// <param name="Reason">Why, in one line, shown as the column's tooltip. Always set when
/// <paramref name="Level"/> is not <see cref="CpeConfidenceLevel.None"/>, because a bare word like
/// "Low" tells a reviewer nothing about what to do next.</param>
public record CpeConfidenceAssessment(CpeConfidenceLevel Level, string? Reason);

/// <summary>
/// Scores a proposed mapping from what this system already knows, so a reviewer draining the queue
/// can tell the obvious rows from the ones that need reading.
/// </summary>
/// <remarks>
/// <para>
/// <b>Computed, never stored.</b> It is a function of the display name, the vendor and product and
/// how they were arrived at — all of which are already on the row — so persisting it would create a
/// second copy to fall out of step with the first the moment somebody re-maps a subject.
/// </para>
/// <para>
/// <b>It is not the model's opinion of itself.</b> The AI is never asked how sure it is: a
/// self-reported score has no external authority behind it, which is the same reason
/// <c>VantaResourceBuilder</c> keeps a model away from a severity number. What is scored here is
/// evidence anybody can re-derive — whether the dictionary's product name is recognisably the name
/// the fleet reports, and whether a person has already decided. Every suggestion in the queue
/// exists in NVD's dictionary already (<c>RunVulnerabilityAssessmentCommandHandler</c> discards the
/// ones that do not), so existence is not the question. The question is whether it is *this*
/// product: <c>a:slack:slack</c> and <c>a:slackware:slackware_linux</c> both exist.
/// </para>
/// </remarks>
public static class CpeConfidence
{
    public static CpeConfidenceAssessment Assess(
        string displayName,
        string? vendor,
        string? product,
        CpeMappingStatus status,
        CpeSuggestionSource source)
    {
        if (vendor is null || product is null)
        {
            return new CpeConfidenceAssessment(CpeConfidenceLevel.None, null);
        }

        if (status == CpeMappingStatus.Confirmed)
        {
            // A person deciding is the strongest evidence this system can hold, and stronger than
            // any string comparison — the reviewer had the dictionary, the installed versions and
            // the product's own website in front of them.
            return new CpeConfidenceAssessment(
                CpeConfidenceLevel.High,
                source == CpeSuggestionSource.Manual
                    ? "Confirmed by a person."
                    // SourceWords carries its own article — "an AI suggestion", "NVD's
                    // dictionary" — so this must not supply a second one.
                    : $"Confirmed by a person, from {SourceWords(source)}.");
        }

        var name = Normalize(displayName);
        var normalizedProduct = Normalize(product);
        var normalizedVendor = Normalize(vendor);
        var sourceSuffix = source == CpeSuggestionSource.None ? "" : $" Proposed by {SourceWords(source)}.";

        // The name the fleet reports *is* the product NVD indexes, give or take spacing, case and
        // CPE's underscores — "Google Chrome" against google:chrome, "Mozilla Firefox" against
        // mozilla:firefox. Nothing left to read.
        if (name == normalizedProduct || name == normalizedVendor + normalizedProduct)
        {
            return new CpeConfidenceAssessment(
                CpeConfidenceLevel.High,
                $"The reported name matches {vendor}:{product} exactly.{sourceSuffix}");
        }

        var nameWords = Words(displayName);
        var productWords = Words(product);

        // Whole words, and *only* whole words. Plain substring containment is what makes this
        // feature necessary in the first place: "slack" sits inside slackware_linux and "zoom"
        // inside zoomtext, so a containment test marks the two mappings this screen exists to
        // prevent as partial matches. Splitting CPE's underscores first keeps the real partials —
        // zoom_workplace_desktop has "zoom" as a word of its own, slackware_linux does not.
        if (nameWords.Overlaps(productWords))
        {
            return new CpeConfidenceAssessment(
                CpeConfidenceLevel.Medium,
                $"The reported name and {product} share a word but are not identical — check this is the same product.{sourceSuffix}");
        }

        // The product is unrecognisable but the vendor is not, which is the shape of a rebranded or
        // suite-renamed product: "Acrobat Pro" against adobe:acrobat_dc.
        if (nameWords.Contains(normalizedVendor))
        {
            return new CpeConfidenceAssessment(
                CpeConfidenceLevel.Medium,
                $"Only the vendor {vendor} appears in the reported name — check {product} is the right product for it.{sourceSuffix}");
        }

        return new CpeConfidenceAssessment(
            CpeConfidenceLevel.Low,
            $"Nothing in the reported name resembles {vendor}:{product}. This is the shape a wrong mapping takes.{sourceSuffix}");
    }

    private static string SourceWords(CpeSuggestionSource source) => source switch
    {
        CpeSuggestionSource.Ai => "an AI suggestion",
        CpeSuggestionSource.Dictionary => "NVD's dictionary",
        CpeSuggestionSource.Manual => "a hand-entered pair",
        _ => "an unrecorded source",
    };

    /// <summary>Lower-cased letters and digits only. CPE separates words with an underscore, the
    /// fleet reports them with spaces, dots and hyphens, and neither difference means anything:
    /// "Visual Studio Code" and <c>visual_studio_code</c> are the same three words.</summary>
    private static string Normalize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>The distinct words of a name, normalized, floored at three characters — "Go" inside
    /// "mongodb" is not evidence of anything, and neither is a version number.</summary>
    private static HashSet<string> Words(string value) => value
        .Split(new[] { '_', '-', ' ', '.', ',', '(', ')', '/', '+', ':' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(Normalize)
        .Where(w => w.Length >= 3)
        .ToHashSet();
}
