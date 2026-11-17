using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class BaselineRepository : IBaselineRepository
{
    private readonly ApplicationDbContext _context;

    public BaselineRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Baseline>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.Baselines.ToListAsync(cancellationToken);

    public Task<Baseline?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Baselines.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

    // Case-insensitive, so "Windows workstation" and "windows workstation" are one baseline, not two.
    public Task<Baseline?> GetByNameAsync(string name, CancellationToken cancellationToken) =>
        _context.Baselines.FirstOrDefaultAsync(b => b.Name.ToLower() == name.ToLower(), cancellationToken);

    public async Task AddAsync(Baseline baseline, CancellationToken cancellationToken) =>
        await _context.Baselines.AddAsync(baseline, cancellationToken);

    public async Task<IReadOnlyList<BaselineVersion>> GetVersionsAsync(IReadOnlyCollection<Guid> baselineIds, CancellationToken cancellationToken) =>
        await _context.BaselineVersions
            .Where(v => baselineIds.Contains(v.BaselineId))
            .OrderByDescending(v => v.Number)
            .ToListAsync(cancellationToken);

    // Two queries rather than one GroupBy, so it translates the same on every provider: the numbers of
    // the signed versions first (no documents), then the one document per baseline that is served.
    public async Task<IReadOnlyList<BaselineVersion>> GetServedVersionsAsync(IReadOnlyCollection<Guid> baselineIds, CancellationToken cancellationToken)
    {
        var signed = await _context.BaselineVersions
            .Where(v => baselineIds.Contains(v.BaselineId) && v.Signature != null)
            .Select(v => new { v.Id, v.BaselineId, v.Number })
            .ToListAsync(cancellationToken);
        var servedIds = signed.GroupBy(v => v.BaselineId).Select(g => g.MaxBy(v => v.Number)!.Id).ToList();

        return servedIds.Count == 0
            ? Array.Empty<BaselineVersion>()
            : await _context.BaselineVersions.Where(v => servedIds.Contains(v.Id)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetVersionIdsAsync(Guid baselineId, CancellationToken cancellationToken) =>
        await _context.BaselineVersions.Where(v => v.BaselineId == baselineId).Select(v => v.Id).ToListAsync(cancellationToken);

    public Task<BaselineVersion?> GetVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
        _context.BaselineVersions.FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken);

    public async Task AddVersionAsync(BaselineVersion version, CancellationToken cancellationToken) =>
        await _context.BaselineVersions.AddAsync(version, cancellationToken);

    public async Task<IReadOnlyList<HostBaselineItemState>> GetStatesAsync(IReadOnlyCollection<Guid> versionIds, CancellationToken cancellationToken) =>
        await _context.HostBaselineItemStates.AsNoTracking()
            .Where(s => versionIds.Contains(s.BaselineVersionId))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<HostBaselineItemState>> GetHostStatesAsync(Guid hostId, IReadOnlyCollection<Guid> versionIds, CancellationToken cancellationToken) =>
        await _context.HostBaselineItemStates
            .Where(s => s.HostId == hostId && versionIds.Contains(s.BaselineVersionId))
            .ToListAsync(cancellationToken);

    public async Task AddStateAsync(HostBaselineItemState state, CancellationToken cancellationToken) =>
        await _context.HostBaselineItemStates.AddAsync(state, cancellationToken);

    public void RemoveStates(IEnumerable<HostBaselineItemState> states) =>
        _context.HostBaselineItemStates.RemoveRange(states);

    public async Task<IReadOnlyList<HostFact>> GetHostFactsAsync(Guid hostId, CancellationToken cancellationToken) =>
        await _context.HostFacts.Where(f => f.HostId == hostId).ToListAsync(cancellationToken);

    public async Task AddFactAsync(HostFact fact, CancellationToken cancellationToken) =>
        await _context.HostFacts.AddAsync(fact, cancellationToken);
}
