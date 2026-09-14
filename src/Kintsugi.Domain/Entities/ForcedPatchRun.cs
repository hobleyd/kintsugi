using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// A standing instruction for one host to run one application's upgrade script at its next
/// opportunity, rather than waiting for that host's own patching schedule to come round. Raised
/// from the Applications screen's "Patch now" action, against the hosts the table is currently
/// filtered to.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The server never pushes; this row is what an agent finds when it looks.</strong> Nothing
/// in this system opens a connection to a managed host — the whole fleet is agents polling in, and
/// the one standing socket that does exist (remote control) is held by the per-user process, so it
/// is absent on exactly the headless servers an emergency is most likely to be about. So a forced
/// run is a row an agent collects from <c>GET /api/forced-patch-runs</c>: within
/// <c>AGENT_POLL_INTERVAL</c> (60s) on a host somebody is logged in to, and at the next hourly
/// check-in on a headless Linux server. It is not instantaneous, and the admin UI says so rather
/// than implying otherwise.
/// </para>
/// <para>
/// <strong>It expires, and that is the point of <see cref="ExpiresUtc"/>.</strong> A laptop that is
/// shut in a bag for three weeks would otherwise come back and start a five-minute countdown for an
/// emergency that was over a fortnight ago. <see cref="DefaultLifetime"/> is long enough to cover a
/// headless server's hourly check-in many times over and short enough that the instruction dies
/// with the emergency that raised it.
/// </para>
/// <para>
/// <strong>Collected once, not repeatedly.</strong> <see cref="MarkCollected"/> is stamped when an
/// agent reads the row, so the next poll a minute later does not hand it the same instruction
/// again — a forced run re-served every 60s would put a host into a five-minute patching warning
/// once a minute for as long as the row lived. The cost of that choice is stated rather than
/// hidden: an agent that is collected from and then dies before it patches has lost the
/// instruction, and the operator presses the button again. That is the better failure of the two.
/// </para>
/// <para>
/// <strong>This is an urgency override, not a trust override.</strong> The row names an application
/// and nothing else — no script travels with it. The agent still fetches its work list through
/// <c>GET /api/upgrade-paths</c> and still runs <c>is_patchable</c>, which verifies the script's
/// signature against the artifact-signing key pinned at enrollment. A forced run cannot make an
/// unsigned script runnable, and must never be given a way to.
/// </para>
/// </remarks>
public class ForcedPatchRun : BaseEntity
{
    /// <summary>How long a forced run stays collectable. See the remarks on this class.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(24);

    public Guid HostId { get; private set; }

    /// <summary>The application whose upgrade script is to be run, named exactly as the inventory
    /// reports it — the same string the agent matches its own work list on.</summary>
    public string ApplicationName { get; private set; } = default!;

    /// <summary>The <c>UpgradePath</c> platform bucket the script lives under ("macOS", "Windows",
    /// "pm:Homebrew", ...). Recorded so the row says <em>which</em> script was forced, since one
    /// application name can have a path per platform and the operator pressed the button on one of
    /// them. The agent does not match on it — it resolves its own bucket for this host the same way
    /// it does for a scheduled cycle — so a mismatch here cannot make the wrong script run.</summary>
    public string Platform { get; private set; } = default!;

    public DateTimeOffset RequestedUtc { get; private set; }

    public DateTimeOffset ExpiresUtc { get; private set; }

    /// <summary>When an agent collected this instruction, or null while it is still waiting to be
    /// found. Never cleared: the row is the record that the host was told.</summary>
    public DateTimeOffset? CollectedUtc { get; private set; }

    private ForcedPatchRun()
    {
    }

    public static ForcedPatchRun Request(Guid hostId, string applicationName, string platform, DateTimeOffset requestedUtc, TimeSpan lifetime)
    {
        if (hostId == Guid.Empty)
        {
            throw new DomainException("A forced patch run must name the host it is for.");
        }

        if (string.IsNullOrWhiteSpace(applicationName))
        {
            throw new DomainException("A forced patch run must name the application to patch.");
        }

        if (string.IsNullOrWhiteSpace(platform))
        {
            throw new DomainException("A forced patch run must name the platform bucket its script is stored under.");
        }

        if (lifetime <= TimeSpan.Zero)
        {
            throw new DomainException("A forced patch run must stay collectable for some length of time.");
        }

        return new ForcedPatchRun
        {
            HostId = hostId,
            ApplicationName = applicationName,
            Platform = platform,
            RequestedUtc = requestedUtc,
            ExpiresUtc = requestedUtc + lifetime
        };
    }

    /// <summary>Whether an agent asking at <paramref name="now"/> should be handed this row.</summary>
    public bool IsCollectableAt(DateTimeOffset now) => CollectedUtc is null && now < ExpiresUtc;

    /// <summary>
    /// Pushes a row's clock forward instead of opening a second one for the same
    /// (host, application, platform) — but only while it is still waiting to be collected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two presses of the button thirty seconds apart, before any agent has looked, mean "patch
    /// this, now" once; a second row would sit there and be handed over on the poll *after* the one
    /// that patches.
    /// </para>
    /// <para>
    /// <strong>A press after the instruction has been collected is deliberately a new instruction,
    /// not a renewal of that one.</strong> The first is already being carried out — quite possibly
    /// failing — and an operator pressing again in an emergency means it, so
    /// <c>IForcedPatchRunRepository.GetOutstandingAsync</c> looks only at uncollected rows and the
    /// handler opens a fresh one. That costs nothing when the first attempt worked: the agent
    /// re-plans, finds the application already current, and stops before it shows anyone a second
    /// warning (see each agent's <c>patch_cycle::run_forced</c>).
    /// </para>
    /// <para>
    /// So the guard below is unreachable from the handler, and is here to keep it that way: a
    /// second lookup that stopped excluding collected rows would otherwise silently turn a
    /// deliberate re-press into a no-op.
    /// </para>
    /// </remarks>
    public void Renew(DateTimeOffset requestedUtc, TimeSpan lifetime)
    {
        if (CollectedUtc is not null)
        {
            throw new DomainException("A forced patch run that an agent has already collected cannot be renewed; request a new one.");
        }

        RequestedUtc = requestedUtc;
        ExpiresUtc = requestedUtc + lifetime;
        MarkUpdated();
    }

    /// <summary>
    /// Records that an agent has been handed this instruction. Idempotent — a retried request that
    /// arrives twice must not move the timestamp, which is the record of when the host was actually
    /// told.
    /// </summary>
    public void MarkCollected(DateTimeOffset collectedUtc)
    {
        if (CollectedUtc is not null)
        {
            return;
        }

        CollectedUtc = collectedUtc;
        MarkUpdated();
    }
}
