using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class ApprovedScriptRepository : IApprovedScriptRepository
{
    private readonly ApplicationDbContext _context;

    public ApprovedScriptRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<ApprovedScript?> GetAsync(string sha256, string signerFingerprint, CancellationToken cancellationToken) =>
        _context.ApprovedScripts.FirstOrDefaultAsync(
            s => s.Sha256 == sha256 && s.SignerFingerprint == signerFingerprint, cancellationToken);

    public async Task AddAsync(ApprovedScript approvedScript, CancellationToken cancellationToken) =>
        await _context.ApprovedScripts.AddAsync(approvedScript, cancellationToken);

    public async Task<IReadOnlyList<ApprovedScript>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.ApprovedScripts
            .AsNoTracking()
            .OrderBy(s => s.ApplicationName)
            .ThenBy(s => s.PlatformBucket)
            .ToListAsync(cancellationToken);

    // Distinct hashes only, projected in SQL: blessing needs to know *whether* a local row's content
    // is approved, and pulling every approved script's full text back to answer that would scale with
    // the corpus for no reason.
    public async Task<IReadOnlyCollection<string>> GetApprovedContentHashesAsync(CancellationToken cancellationToken) =>
        await _context.ApprovedScripts
            .AsNoTracking()
            .Select(s => s.Sha256)
            .Distinct()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ApprovedScript>> GetForApplicationAsync(
        string applicationName, string platformBucket, CancellationToken cancellationToken) =>
        await _context.ApprovedScripts
            .AsNoTracking()
            .Where(s => s.ApplicationName == applicationName && s.PlatformBucket == platformBucket)
            .OrderByDescending(s => s.ApprovedAtUtc)
            .ToListAsync(cancellationToken);
}
