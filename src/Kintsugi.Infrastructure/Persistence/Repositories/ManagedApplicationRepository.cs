using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class ManagedApplicationRepository : IManagedApplicationRepository
{
    private readonly ApplicationDbContext _context;

    public ManagedApplicationRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<ManagedApplication>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.ManagedApplications.OrderBy(a => a.ApplicationName).ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> GetOwnersByNameAsync(CancellationToken cancellationToken) =>
        (await _context.ManagedApplications.Select(a => new { a.ApplicationName, a.Owner }).ToListAsync(cancellationToken))
        .ToDictionary(a => a.ApplicationName, a => a.Owner, StringComparer.OrdinalIgnoreCase);

    public Task<ManagedApplication?> FindAsync(string applicationName, CancellationToken cancellationToken)
    {
        var normalized = ManagedApplication.Normalize(applicationName);
        return _context.ManagedApplications.FirstOrDefaultAsync(a => a.NormalizedName == normalized, cancellationToken);
    }

    public async Task AddAsync(ManagedApplication application, CancellationToken cancellationToken) =>
        await _context.ManagedApplications.AddAsync(application, cancellationToken);

    public void Remove(ManagedApplication application) => _context.ManagedApplications.Remove(application);

    public Task<bool> OwnerIsInUseAsync(string owner, CancellationToken cancellationToken)
    {
        var normalized = ManagedApplication.Normalize(owner);
        // Owner is stored as the list spells it, but compared the way names always are.
        return _context.ManagedApplications.AnyAsync(a => a.Owner.ToLower() == normalized, cancellationToken);
    }

    public async Task<IReadOnlyList<ManagedOwner>> GetOwnersAsync(CancellationToken cancellationToken) =>
        await _context.ManagedOwners.OrderBy(o => o.Name).ToListAsync(cancellationToken);

    public Task<ManagedOwner?> FindOwnerAsync(string name, CancellationToken cancellationToken)
    {
        var normalized = ManagedApplication.Normalize(name);
        return _context.ManagedOwners.FirstOrDefaultAsync(o => o.NormalizedName == normalized, cancellationToken);
    }

    public async Task AddOwnerAsync(ManagedOwner owner, CancellationToken cancellationToken) =>
        await _context.ManagedOwners.AddAsync(owner, cancellationToken);

    public void RemoveOwner(ManagedOwner owner) => _context.ManagedOwners.Remove(owner);
}
