using System.Text.Json.Serialization;

namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// What a fleet-wide run concluded about one row, in the words the Diagnostics page's tabs use.
/// </summary>
/// <remarks>
/// One enum for both runs, because the page reads them the same way and each run uses only its own
/// half: "Find Upgrade Paths" produces <see cref="Resolved"/>, <see cref="NoKnownPath"/>,
/// <see cref="Failed"/> and <see cref="AlreadyKnown"/>; "Check for Updates" produces
/// <see cref="Updated"/>, <see cref="Unchanged"/>, <see cref="Failed"/> and
/// <see cref="NothingToCheck"/>. The counts on the status DTOs are these, summed — which is why a
/// skip is its own outcome rather than a flavour of failure.
///
/// Crosses the wire as its name, and the web client reads it case-insensitively
/// (<c>web/lib/data/models/upgrade_path_mapper.dart</c>), so an outcome the client does not know
/// shows up uncategorised rather than as a crash. Append, do not reorder or rename.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunRowOutcome
{
    /// <summary>The scan found a usable upgrade path.</summary>
    Resolved,

    /// <summary>The scan finished and found none. A real answer, not an error.</summary>
    NoKnownPath,

    /// <summary>The scan or the check itself failed.</summary>
    Failed,

    /// <summary>The scan skipped it because a path was already resolved.</summary>
    AlreadyKnown,

    /// <summary>The check ran and the version had changed.</summary>
    Updated,

    /// <summary>The check ran and the version was the same.</summary>
    Unchanged,

    /// <summary>The check had nothing to run: no script, or none this server can run.</summary>
    NothingToCheck
}
