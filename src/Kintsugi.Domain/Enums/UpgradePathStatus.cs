using System.Text.Json.Serialization;

namespace Kintsugi.Domain.Enums;

/// <summary>
/// Whether an upgrade path has been resolved for an application, distinguishing a definitive
/// negative result from a technical failure worth retrying.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UpgradePathStatus
{
    /// <summary>A usable upgrade path was found. Not re-researched by later scans.</summary>
    Found,

    /// <summary>The research completed but found no reliable upgrade path — a real answer, not
    /// an error. Still retried by later scans, since a new release may change that.</summary>
    NotFound,

    /// <summary>The check itself failed (network error, malformed response, etc.) rather than
    /// concluding anything about the application. Retried by later scans.</summary>
    Failed
}
