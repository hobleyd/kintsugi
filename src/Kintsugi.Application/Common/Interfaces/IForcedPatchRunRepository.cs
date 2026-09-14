using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IForcedPatchRunRepository
{
    /// <summary>The row already outstanding for this (host, application, platform), if there is one
    /// — what a second press of "Patch now" renews rather than duplicating. Deliberately excludes a
    /// row an agent has already collected: that instruction has been delivered and is being carried
    /// out, so forcing the same application again is a new instruction, not an extension of the old
    /// one. See the remarks on <see cref="Domain.Entities.ForcedPatchRun.Renew"/>, which is the
    /// other half of that decision.</summary>
    Task<ForcedPatchRun?> GetOutstandingAsync(Guid hostId, string applicationName, string platform, CancellationToken cancellationToken);

    /// <summary>Everything one host should be handed right now: raised, not yet collected, not yet
    /// expired. The caller stamps them collected — see <c>ClaimForcedPatchRunsCommandHandler</c>.</summary>
    Task<IReadOnlyList<ForcedPatchRun>> GetCollectableForHostAsync(Guid hostId, DateTimeOffset asOfUtc, CancellationToken cancellationToken);

    Task AddAsync(ForcedPatchRun run, CancellationToken cancellationToken);
}
