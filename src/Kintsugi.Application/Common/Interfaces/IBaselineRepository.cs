using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>Baselines, their signed versions, and what each host reported against them. One repository
/// because every caller reads across the three — see <c>Kintsugi.Application/Baselines</c>.</summary>
public interface IBaselineRepository
{
    Task<IReadOnlyList<Baseline>> GetAllAsync(CancellationToken cancellationToken);

    Task<Baseline?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Baseline?> GetByNameAsync(string name, CancellationToken cancellationToken);

    Task AddAsync(Baseline baseline, CancellationToken cancellationToken);

    /// <summary>Every version of the named baselines, newest first within each.</summary>
    Task<IReadOnlyList<BaselineVersion>> GetVersionsAsync(IReadOnlyCollection<Guid> baselineIds, CancellationToken cancellationToken);

    /// <summary>
    /// The version agents are given for each of the named baselines — the highest-numbered signed one,
    /// the same rule as <c>BaselineCompliance.ServedVersion</c> — with only those versions' documents
    /// read. What both agent routes use: every version's document is what the admin screen needs, and
    /// loading it on every check-in from every host is what this exists to avoid.
    /// </summary>
    Task<IReadOnlyList<BaselineVersion>> GetServedVersionsAsync(IReadOnlyCollection<Guid> baselineIds, CancellationToken cancellationToken);

    /// <summary>Every version id of one baseline, without the documents.</summary>
    Task<IReadOnlyList<Guid>> GetVersionIdsAsync(Guid baselineId, CancellationToken cancellationToken);

    Task<BaselineVersion?> GetVersionAsync(Guid versionId, CancellationToken cancellationToken);

    Task AddVersionAsync(BaselineVersion version, CancellationToken cancellationToken);

    /// <summary>Every host's state rows for the given versions.</summary>
    Task<IReadOnlyList<HostBaselineItemState>> GetStatesAsync(IReadOnlyCollection<Guid> versionIds, CancellationToken cancellationToken);

    /// <summary>One host's state rows for the given versions.</summary>
    Task<IReadOnlyList<HostBaselineItemState>> GetHostStatesAsync(Guid hostId, IReadOnlyCollection<Guid> versionIds, CancellationToken cancellationToken);

    Task AddStateAsync(HostBaselineItemState state, CancellationToken cancellationToken);

    void RemoveStates(IEnumerable<HostBaselineItemState> states);

    Task<IReadOnlyList<HostFact>> GetHostFactsAsync(Guid hostId, CancellationToken cancellationToken);

    Task AddFactAsync(HostFact fact, CancellationToken cancellationToken);
}
