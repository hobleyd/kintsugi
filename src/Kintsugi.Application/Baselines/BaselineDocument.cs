using System.Text.Json;
using System.Text.Json.Serialization;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.Baselines;

/// <summary>
/// One item of a baseline, exactly as it appears in the signed document. Mirrored by hand by
/// <c>BaselineItem</c> in <c>clients/windows-agent/src/baseline.rs</c> (Windows only in this step:
/// registry items mean nothing to the macOS and Linux agents, which do not fetch baselines yet) and
/// read by <c>web/lib/data/models/baseline_mapper.dart</c>. See <c>.claude/rules/hand-mirrored-dtos.md</c>.
/// </summary>
/// <remarks>
/// Kinds, scopes, hives, value types and modes are lower-case strings rather than C# enums: this is a
/// document a person reads before signing, and <c>"dword"</c> says more there than <c>2</c> does. The
/// accepted spellings are the constants on <see cref="BaselineItemRules"/>.
/// </remarks>
/// <param name="Key">Stable id within the baseline (<c>copilot.turn-off</c>). Results are reported
/// against it, so a key keeps its meaning from version to version.</param>
/// <param name="Kind"><c>setting</c> (a value that should hold) or <c>fact</c> (a value that is only read
/// and reported).</param>
/// <param name="Scope"><c>machine</c> (read by the service) or <c>user</c> (the tray's, in a later step).</param>
/// <param name="Hive"><c>HKLM</c> for machine scope, <c>HKCU</c> for user scope.</param>
/// <param name="Path">The key path under the hive, backslash-separated, no leading backslash.</param>
/// <param name="ValueName">The value's name; empty for the key's default value.</param>
/// <param name="ValueType">Settings only: <c>dword</c>, <c>qword</c>, <c>string</c>, <c>expandString</c>,
/// <c>multiString</c> or <c>binary</c>.</param>
/// <param name="Data">Settings only: the expected data as text — decimal for <c>dword</c>/<c>qword</c>,
/// lower-case hex for <c>binary</c>, one line per string for <c>multiString</c>. Null when
/// <paramref name="Secret"/> names the value instead.</param>
/// <param name="Secret">Settings only: the name of a secret that supplies the value. Secrets arrive in a
/// later step, so in this one the agent reports such an item not-applicable and never reads it.</param>
/// <param name="Mode">Settings only: <c>audit</c> (read and report) or <c>enforce</c> (also set it when it
/// has drifted), the second only for a value on <see cref="BaselineItemRules.EnforceableValues"/>.</param>
/// <param name="Reason">Why this item exists: its source and the control it evidences.</param>
public sealed record BaselineItem(
    string Key,
    string Kind,
    string Scope,
    string Hive,
    string Path,
    string ValueName,
    string? ValueType = null,
    string? Data = null,
    string? Secret = null,
    string? Mode = null,
    string? Reason = null)
{
    [JsonIgnore]
    public bool IsSetting => Kind == BaselineItemRules.KindSetting;

    [JsonIgnore]
    public bool IsFact => Kind == BaselineItemRules.KindFact;
}

/// <summary>
/// The document a <c>BaselineVersion</c> stores and the server signs: every item, plus the ids that
/// say which baseline and version it is. The agent verifies the signature over the exact string it
/// receives and only then parses it.
/// </summary>
/// <remarks>
/// <strong>Canonical, so the same items always make the same bytes.</strong> <see cref="ToCanonicalJson"/>
/// sorts items by key, writes properties in declaration order, omits nulls, indents nothing and
/// escapes everything outside ASCII. That makes the SHA-256 a reviewer is shown a function of what the
/// baseline says rather than of how a client happened to send it, and keeps the stored text immune to
/// any encoding step between here and an agent.
/// </remarks>
public sealed record BaselineDocument(
    int Schema,
    Guid BaselineId,
    string Baseline,
    Guid VersionId,
    int Version,
    IReadOnlyList<BaselineItem> Items)
{
    public const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string ToCanonicalJson(BaselineDocument document) =>
        JsonSerializer.Serialize(
            document with { Items = document.Items.OrderBy(i => i.Key, StringComparer.Ordinal).ToList() },
            Options);

    /// <summary>Reads a stored document back. A document this server wrote always parses; one that does
    /// not was changed outside it, which is a refusal rather than a best effort.</summary>
    public static BaselineDocument Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BaselineDocument>(json, Options)
                ?? throw new DomainException("The stored baseline document is empty.");
        }
        catch (JsonException ex)
        {
            throw new DomainException($"The stored baseline document is not valid: {ex.Message}");
        }
    }
}
