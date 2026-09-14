using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class ForcedPatchRunRepository : IForcedPatchRunRepository
{
    private readonly ApplicationDbContext _context;

    public ForcedPatchRunRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    // Case-insensitive on ApplicationName, matching PatchFailureRepository and UpgradePathRepository
    // — the agents match applications that way everywhere, and a case-sensitive lookup here would
    // open a second row for the same application the day an inventory report settled on different
    // casing.
    public Task<ForcedPatchRun?> GetOutstandingAsync(Guid hostId, string applicationName, string platform, CancellationToken cancellationToken) =>
        _context.ForcedPatchRuns
            .Where(r => r.HostId == hostId
                && r.ApplicationName.ToLower() == applicationName.ToLower()
                && r.Platform == platform
                && r.CollectedUtc == null)
            .OrderByDescending(r => r.RequestedUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ForcedPatchRun>> GetCollectableForHostAsync(Guid hostId, DateTimeOffset asOfUtc, CancellationToken cancellationToken) =>
        await _context.ForcedPatchRuns
            .Where(r => r.HostId == hostId && r.CollectedUtc == null && r.ExpiresUtc > asOfUtc)
            .OrderBy(r => r.RequestedUtc)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(ForcedPatchRun run, CancellationToken cancellationToken) =>
        await _context.ForcedPatchRuns.AddAsync(run, cancellationToken);
}
