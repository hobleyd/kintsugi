using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// The link between something this fleet has installed and the CPE name NVD indexes vulnerabilities
/// under. One row per distinct subject, fleet-wide.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the whole hard part of vulnerability assessment.</b> NVD keys every CVE to
/// <c>cpe:2.3:&lt;part&gt;:&lt;vendor&gt;:&lt;product&gt;:&lt;version&gt;</c> and does the
/// version-range matching itself, so once a subject has a confirmed vendor and product the rest is
/// a query. Getting to that vendor and product is not mechanical: NVD's own CPE dictionary keyword
/// search ranks Slackware Linux first for "slack" and ZoomText first for "zoom", and Kintsugi's
/// inventory carries a display name, not a vendor. So a mapping is proposed by machine and
/// <b>confirmed by a human</b>, and nothing is assessed until it has been.
/// </para>
/// <para>
/// <b>Keyed on the reported name, never on <see cref="InstalledApplication"/>.</b>
/// <c>RegisterApplicationsCommandHandler</c> deletes and recreates every installed-application row
/// on each routine inventory report, so a mapping keyed on one of those ids would evaporate every
/// hour and a human's confirmation with it. This is the same reasoning that makes Vanta's
/// <c>uniqueId</c> a (serial, application name) pair rather than a row id — see src/CLAUDE.md.
/// </para>
/// <para>
/// A subject with no CPE is not hidden. <see cref="CpeMappingStatus.Unmapped"/> rows are counted on
/// the Vulnerabilities screen as applications nothing has been assessed for, because a screen that
/// lists only what it managed to match reads as full coverage and is exactly the failure this
/// codebase refuses elsewhere (see <c>VantaResourceBuilder</c> and the eleven Vanta resource types
/// deliberately left unsynced).
/// </para>
/// </remarks>
public class CpeMapping : BaseEntity
{
    /// <summary>Whether this maps an application or a host operating system — which also decides
    /// the CPE part letter. See <see cref="Part"/>.</summary>
    public CpeSubjectKind SubjectKind { get; private set; }

    /// <summary>
    /// The normalized key this subject is recognized by: an application's reported name folded to
    /// lower case, or the OS product token <c>OperatingSystemSubject</c> derived from
    /// <c>Host.OperatingSystem</c>. Normalized so that "Google Chrome" and "Google chrome" are one
    /// mapping rather than two rows a human has to confirm separately.
    /// </summary>
    public string SubjectKey { get; private set; } = default!;

    /// <summary>The subject as a human would recognize it — the reported name with its original
    /// casing, or a readable OS name. Display only; <see cref="SubjectKey"/> is the identity.</summary>
    public string DisplayName { get; private set; } = default!;

    /// <summary>The CPE vendor, e.g. <c>mozilla</c>. Null until something has been proposed.</summary>
    public string? Vendor { get; private set; }

    /// <summary>The CPE product, e.g. <c>firefox</c>. Null until something has been proposed.</summary>
    public string? Product { get; private set; }

    public CpeMappingStatus Status { get; private set; } = CpeMappingStatus.Unmapped;

    public CpeSuggestionSource SuggestionSource { get; private set; } = CpeSuggestionSource.None;

    /// <summary>Whatever the proposer wanted the reviewer to know — the model's one-line reasoning,
    /// or how many dictionary candidates were passed over. Shown beside the Confirm button.</summary>
    public string? SuggestionNotes { get; private set; }

    /// <summary>When a human accepted the vendor and product. Null in every other state.</summary>
    public DateTimeOffset? ConfirmedAtUtc { get; private set; }

    private CpeMapping()
    {
    }

    /// <summary>Records a subject discovered in the inventory, with nothing yet known about its
    /// CPE. Discovery is idempotent — see <c>DiscoverCpeSubjectsCommandHandler</c>, which only
    /// creates a row when no mapping already holds the key.</summary>
    public static CpeMapping Discover(CpeSubjectKind subjectKind, string subjectKey, string displayName)
    {
        if (string.IsNullOrWhiteSpace(subjectKey))
        {
            throw new DomainException("A CPE mapping needs a subject key.");
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new DomainException("A CPE mapping needs a display name.");
        }

        return new CpeMapping
        {
            SubjectKind = subjectKind,
            SubjectKey = Normalize(subjectKey),
            DisplayName = displayName.Trim()
        };
    }

    /// <summary>
    /// The part letter this subject's CPE names are built with — <c>a</c> for an application,
    /// <c>o</c> for an operating system. Derived rather than stored, because it is a function of
    /// <see cref="SubjectKind"/> and a row where the two disagreed would query NVD for something
    /// that cannot exist.
    /// </summary>
    public string Part => SubjectKind == CpeSubjectKind.OperatingSystem ? "o" : "a";

    /// <summary>
    /// The CPE 2.3 name for this subject at <paramref name="version"/>, in the form NVD's
    /// <c>virtualMatchString</c> parameter expects. NVD evaluates the configuration ranges against
    /// the version itself, which is why nothing in this codebase parses
    /// <c>versionStartIncluding</c>/<c>versionEndExcluding</c>.
    /// </summary>
    public string ToCpeName(string version)
    {
        if (Vendor is null || Product is null)
        {
            throw new DomainException($"'{DisplayName}' has no CPE vendor and product to build a name from.");
        }

        return $"cpe:2.3:{Part}:{Vendor}:{Product}:{EscapeComponent(version)}:*:*:*:*:*:*:*";
    }

    /// <summary>The vendor-and-product-only form, used to ask the CPE dictionary whether a proposed
    /// pair names anything at all. This is what stops an AI suggestion being taken on trust.</summary>
    public static string ToCpeMatchString(string part, string vendor, string product) =>
        $"cpe:2.3:{part}:{EscapeComponent(vendor)}:{EscapeComponent(product)}:*:*:*:*:*:*:*:*";

    /// <summary>Proposes a vendor and product without accepting them. The caller is responsible for
    /// having checked that the pair exists in NVD's dictionary first — see
    /// <c>ICpeDictionaryClient.ExistsAsync</c>, which is what makes a model's guess safe to store.</summary>
    public void Suggest(string vendor, string product, CpeSuggestionSource source, string? notes)
    {
        if (Status == CpeMappingStatus.Confirmed)
        {
            // A confirmation is a human decision and a suggestion is a machine's; letting the
            // second overwrite the first would silently re-point findings at a different product
            // between one run and the next.
            throw new DomainException($"'{DisplayName}' is already confirmed; re-map it explicitly instead.");
        }

        if (source == CpeSuggestionSource.None)
        {
            throw new DomainException("A suggestion needs a source.");
        }

        Vendor = RequireComponent(vendor, nameof(vendor));
        Product = RequireComponent(product, nameof(product));
        SuggestionSource = source;
        SuggestionNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        Status = CpeMappingStatus.Suggested;
        ConfirmedAtUtc = null;
        MarkUpdated();
    }

    /// <summary>
    /// Accepts a vendor and product — optionally different from whatever was suggested, since the
    /// reviewer may have corrected it. This is the only transition that makes a subject eligible
    /// for assessment.
    /// </summary>
    public void Confirm(string vendor, string product, CpeSuggestionSource source)
    {
        var newVendor = RequireComponent(vendor, nameof(vendor));
        var newProduct = RequireComponent(product, nameof(product));

        // Re-pointing a confirmed mapping invalidates every finding stored against it, which the
        // handler acts on by clearing this mapping's assessments. Reported here rather than
        // inferred there so the rule lives with the invariant.
        Repointed = Status == CpeMappingStatus.Confirmed && (Vendor != newVendor || Product != newProduct);

        Vendor = newVendor;
        Product = newProduct;
        SuggestionSource = source;
        Status = CpeMappingStatus.Confirmed;
        ConfirmedAtUtc = DateTimeOffset.UtcNow;
        MarkUpdated();
    }

    /// <summary>
    /// Whether the last <see cref="Confirm"/> moved an already-confirmed mapping to a different
    /// vendor or product, so its stored findings describe a product this subject no longer claims
    /// to be. Not persisted — it is a fact about the call, read by the handler in the same unit of
    /// work to decide whether to discard the assessments.
    /// </summary>
    public bool Repointed { get; private set; }

    /// <summary>Records that this subject has no meaningful CPE. Keeps the row so discovery does
    /// not re-raise it every run, and takes it out of the "not assessed" count, because a decision
    /// is not a gap.</summary>
    public void MarkNotApplicable(string? notes)
    {
        Vendor = null;
        Product = null;
        Status = CpeMappingStatus.NotApplicable;
        SuggestionSource = CpeSuggestionSource.None;
        SuggestionNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        ConfirmedAtUtc = null;
        MarkUpdated();
    }

    /// <summary>Returns a mapping to the queue — for a subject marked not-applicable in error, or a
    /// suggestion a reviewer rejected without having a better one to hand.</summary>
    public void Reset()
    {
        Vendor = null;
        Product = null;
        Status = CpeMappingStatus.Unmapped;
        SuggestionSource = CpeSuggestionSource.None;
        SuggestionNotes = null;
        ConfirmedAtUtc = null;
        MarkUpdated();
    }

    /// <summary>Keeps the human-facing label in step with what the fleet currently calls this
    /// subject, without disturbing the mapping itself — a renamed application is the same
    /// application.</summary>
    public void UpdateDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim() == DisplayName)
        {
            return;
        }

        DisplayName = displayName.Trim();
        MarkUpdated();
    }

    /// <summary>
    /// Records that something looked at this subject and had nothing to add — the AI declining to
    /// propose a CPE for an in-house tool, most often.
    /// </summary>
    /// <remarks>
    /// It exists only to move the row's <c>UpdatedAtUtc</c>, which is what
    /// <c>GetMappingsByStatusAsync</c> orders the suggestion queue by. Without it a subject the
    /// model cannot map sits at the head of that queue forever, is re-asked on every run, and
    /// starves every subject behind it — spending a model call per run to be told the same thing.
    /// </remarks>
    public void Touch() => MarkUpdated();

    /// <summary>Whether this mapping can be handed to NVD as a query.</summary>
    public bool IsAssessable => Status == CpeMappingStatus.Confirmed && Vendor is not null && Product is not null;

    public static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static string RequireComponent(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"A CPE {field} is required.");
        }

        var normalized = value.Trim().ToLowerInvariant();

        // A CPE component is a restricted alphabet, and a colon or an asterisk smuggled into one
        // would change the shape of every CPE name built from it — turning a query for one product
        // into a query for something else entirely.
        if (normalized.Any(c => c is ':' or '*' or '?' or ' '))
        {
            throw new DomainException($"A CPE {field} may not contain spaces, colons or wildcards.");
        }

        return normalized;
    }

    /// <summary>
    /// Escapes the characters CPE 2.3 formatted strings treat as special. Version strings routinely
    /// carry colons and Homebrew tokens carry <c>@</c>, and an unescaped colon would shift every
    /// later component one place along — a query for a version becoming a query for an edition.
    /// </summary>
    private static string EscapeComponent(string value)
    {
        var escaped = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is ':' or '*' or '?' or '\\' or '!' or '"' or '#' or '$' or '%' or '&' or '\'' or '(' or ')'
                or '+' or ',' or '/' or ';' or '<' or '=' or '>' or '@' or '[' or ']' or '^' or '`' or '{' or '|'
                or '}' or '~')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }
}
