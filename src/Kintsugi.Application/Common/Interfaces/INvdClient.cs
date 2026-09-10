namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Queries the NVD 2.0 APIs — CVEs affecting a given CPE name, and the CPE dictionary that says
/// whether a proposed vendor and product name anything at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>NVD evaluates the version ranges, and that is the load-bearing fact of this whole feature.</b>
/// Passing <c>virtualMatchString</c> a CPE name carrying a concrete version returns only the CVEs
/// whose configurations actually cover that version — verified against the live API: Firefox
/// returns 3337 CVEs at <c>*</c>, 1508 at 60.0, 631 at 130.0 and 414 at 145.0. So nothing in this
/// codebase parses <c>versionStartIncluding</c> or <c>versionEndExcluding</c>, and nothing should
/// start: re-implementing that matching locally would be a second, divergent opinion about which
/// versions a CVE affects.
/// </para>
/// <para>
/// <b>Rate limits shape every caller.</b> NVD allows 5 requests per rolling 30 seconds anonymously
/// and 50 with a free API key. An implementation must pace itself accordingly, and the assessment
/// loop is bounded and resumable because of it — see
/// <c>VulnerabilitySettings.AssessmentsPerRun</c>.
/// </para>
/// </remarks>
public interface INvdClient
{
    /// <summary>
    /// Every CVE NVD says affects <paramref name="cpeName"/> — a CPE 2.3 name with a concrete
    /// version, as <c>CpeMapping.ToCpeName</c> builds. Pages until the reported total is covered:
    /// a badly out-of-date install genuinely exceeds one page, and truncating there would drop
    /// findings for exactly the hosts that most need them.
    /// </summary>
    Task<IReadOnlyList<NvdCveRecord>> GetCvesForCpeAsync(string cpeName, string? apiKey, CancellationToken cancellationToken);

    /// <summary>
    /// Whether NVD's CPE dictionary contains any product matching <paramref name="cpeMatchString"/>
    /// — a vendor-and-product-only CPE name, as <c>CpeMapping.ToCpeMatchString</c> builds.
    /// </summary>
    /// <remarks>
    /// This is what makes it safe to let a language model propose a CPE. The model supplies a
    /// candidate search term and NVD is the authority on whether that term names a real product:
    /// <c>a:mozilla:firefox</c> answers 1199, the invented <c>a:mozilla:firefax</c> answers 0, and
    /// so does the plausible-but-wrong <c>a:slack:slack</c>. A suggestion that fails this check is
    /// discarded rather than shown to a reviewer, so a hallucination never becomes something a
    /// human is invited to rubber-stamp.
    /// </remarks>
    Task<bool> CpeExistsAsync(string cpeMatchString, string? apiKey, CancellationToken cancellationToken);

    /// <summary>
    /// Candidate products from NVD's CPE dictionary for a free-text name, deduplicated to distinct
    /// (part, vendor, product) triples and ordered by how many dictionary entries each covers.
    /// </summary>
    /// <remarks>
    /// A weak signal on its own, which is exactly why its results are offered to a human rather
    /// than adopted: searching the live dictionary for "slack" ranks Slackware Linux first, and
    /// "zoom" ranks ZoomText and Zoom Player ahead of Zoom itself.
    /// </remarks>
    Task<IReadOnlyList<CpeCandidate>> SearchCpeDictionaryAsync(string keyword, string? apiKey, CancellationToken cancellationToken);
}

/// <param name="CvssVersion">Which CVSS revision <paramref name="CvssBaseScore"/> came from. NVD
/// publishes several per CVE; an implementation should prefer the newest available, and record
/// which it took, because scores are not comparable across revisions.</param>
public record NvdCveRecord(
    string CveId,
    string? Description,
    double? CvssBaseScore,
    string? CvssVector,
    string? CvssSeverity,
    string? CvssVersion,
    DateTimeOffset? PublishedUtc,
    DateTimeOffset? LastModifiedUtc);

/// <param name="TitleHint">A human-readable title from one of the dictionary entries, when NVD
/// carries one — what lets a reviewer tell <c>a:apple:safari</c> apart from a product whose vendor
/// and product tokens mean nothing to them.</param>
/// <param name="EntryCount">How many dictionary entries this triple covers; the ordering key, and
/// a rough proxy for how established the product is.</param>
public record CpeCandidate(string Part, string Vendor, string Product, string? TitleHint, int EntryCount);
