namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// One row of a fleet-wide run, with what the run concluded about it.
/// </summary>
/// <param name="Note">The reason, where the run had one. A successful row usually has none, which
/// is why the status's own <c>Notes</c> list could never say which rows were fine.</param>
/// <remarks>Hand-mirrored into <c>web/lib/data/models/upgrade_path_mapper.dart</c>; see
/// <c>.claude/rules/hand-mirrored-dtos.md</c>.</remarks>
public record RunRowDto(string ApplicationName, string Platform, RunRowOutcome Outcome, string? Note);

/// <summary>
/// Every row of the current (or most recently completed) run, for the Diagnostics page's tabs.
/// </summary>
/// <param name="Unlisted">How many rows past the coordinator's bound were counted but not kept. The
/// status counts cover all of them; this says how many the list does not.</param>
/// <remarks>
/// A separate request from the run's status, deliberately. The status is polled every three seconds
/// for as long as a run lasts, and a row per application would make every one of those polls carry
/// the whole list. The page asks for this once, when the run finishes.
/// </remarks>
public record UpgradePathRunResultsDto(IReadOnlyList<RunRowDto> Rows, int Unlisted)
{
    public static UpgradePathRunResultsDto Empty { get; } = new(Array.Empty<RunRowDto>(), 0);
}
