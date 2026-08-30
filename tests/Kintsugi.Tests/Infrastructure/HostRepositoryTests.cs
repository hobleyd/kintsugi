using Microsoft.EntityFrameworkCore;
using Kintsugi.Domain.Entities;
using Kintsugi.Infrastructure.Persistence;
using Kintsugi.Infrastructure.Persistence.Repositories;

namespace Kintsugi.Tests.Infrastructure;

public class HostRepositoryTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task AddAsync_ThenGetBySerialNumberAsync_RoundTrips()
    {
        await using var context = CreateContext();
        var repository = new HostRepository(context);
        var host = new Host("host-1", "SERIAL-1", "macOS 15.0");

        await repository.AddAsync(host, CancellationToken.None);
        await context.SaveChangesAsync();

        var found = await repository.GetBySerialNumberAsync("SERIAL-1", CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal("host-1", found!.Hostname);
    }

    [Fact]
    public async Task GetBySerialNumberAsync_ReturnsNull_WhenNoneMatches()
    {
        await using var context = CreateContext();
        var repository = new HostRepository(context);

        var found = await repository.GetBySerialNumberAsync("MISSING", CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsTheMatchingHost()
    {
        await using var context = CreateContext();
        var host = new Host("host-1", "SERIAL-1");
        context.Hosts.Add(host);
        await context.SaveChangesAsync();
        var repository = new HostRepository(context);

        var found = await repository.GetByIdAsync(host.Id, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(host.Id, found!.Id);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsEveryRegisteredHost()
    {
        await using var context = CreateContext();
        context.Hosts.AddRange(new Host("host-1", "SERIAL-1"), new Host("host-2", "SERIAL-2"));
        await context.SaveChangesAsync();
        var repository = new HostRepository(context);

        var all = await repository.GetAllAsync(CancellationToken.None);

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GetAllAsync_ExcludesHostsPendingRemoval()
    {
        await using var context = CreateContext();
        var active = new Host("host-1", "SERIAL-1");
        var removed = new Host("host-2", "SERIAL-2");
        removed.RequestRemoval();
        context.Hosts.AddRange(active, removed);
        await context.SaveChangesAsync();
        var repository = new HostRepository(context);

        var all = await repository.GetAllAsync(CancellationToken.None);

        Assert.Single(all);
        Assert.Equal("SERIAL-1", all[0].SerialNumber);
    }

    [Fact]
    public async Task GetBySerialNumberAsync_StillFindsAHostPendingRemoval()
    {
        // A host stays reachable by serial number after removal is requested — its next check-in
        // still needs to find it, to learn it should uninstall itself. See GetAllAsync, above,
        // for the list-hiding half of the same soft-delete.
        await using var context = CreateContext();
        var host = new Host("host-1", "SERIAL-1");
        host.RequestRemoval();
        context.Hosts.Add(host);
        await context.SaveChangesAsync();
        var repository = new HostRepository(context);

        var found = await repository.GetBySerialNumberAsync("SERIAL-1", CancellationToken.None);

        Assert.NotNull(found);
    }

    [Fact]
    public async Task DeleteAsync_PermanentlyRemovesTheHost()
    {
        await using var context = CreateContext();
        var host = new Host("host-1", "SERIAL-1");
        context.Hosts.Add(host);
        await context.SaveChangesAsync();
        var repository = new HostRepository(context);

        await repository.DeleteAsync(host, CancellationToken.None);
        await context.SaveChangesAsync();

        var found = await repository.GetBySerialNumberAsync("SERIAL-1", CancellationToken.None);
        Assert.Null(found);
    }
}
