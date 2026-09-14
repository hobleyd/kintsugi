namespace Kintsugi.Application.ForcedPatchRuns;

/// <summary>
/// One instruction handed to an agent: patch this application, now. Mirrored by
/// <c>ForcedPatchRun</c> in each agent's <c>forced_patch_run.rs</c>.
/// </summary>
/// <remarks>
/// Carries a name and nothing else — no script, no signature, no version. The agent resolves what
/// to actually run through its ordinary work list (<c>GET /api/upgrade-paths</c>), which is what
/// keeps signature verification on the only path that executes anything. See the remarks on
/// <c>ForcedPatchRun</c>.
/// </remarks>
public record ForcedPatchRunDto(Guid Id, string ApplicationName, string Platform, DateTimeOffset RequestedUtc);

/// <summary>
/// What "Patch now" did, per host it was asked for. Reported back rather than counted, because the
/// interesting half is the hosts it did <em>not</em> reach: a filter can name a host that has since
/// been removed, and an operator acting on an emergency needs to see that the machine they were
/// looking at is not one of the ones that was told.
/// </summary>
public record RequestForcedPatchRunsResult(int Requested, IReadOnlyList<string> NotRequested);
