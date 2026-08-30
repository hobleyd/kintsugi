using Microsoft.EntityFrameworkCore;
using Kintsugi.Domain.Entities;
using Kintsugi.Infrastructure.Persistence;
using Kintsugi.Infrastructure.Persistence.Repositories;

namespace Kintsugi.Tests.Infrastructure;

public class InstalledApplicationRepositoryTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task GetSummariesAsync_CountsDistinctHosts_NotDistinctReportRows()
    {
        await using var context = CreateContext();
        var hostA = new Host("host-a", "SERIAL-A");
        var hostB = new Host("host-b", "SERIAL-B");
        context.Hosts.AddRange(hostA, hostB);
        context.InstalledApplications.AddRange(
            new InstalledApplication(hostA.Id, "Firefox", "128.0"),
            new InstalledApplication(hostB.Id, "Firefox", "127.0")); // same app, different version — still one host each
        await context.SaveChangesAsync();
        var repository = new InstalledApplicationRepository(context);

        var summaries = await repository.GetSummariesAsync(CancellationToken.None);

        Assert.Equal(2, Assert.Single(summaries).HostCount);
    }

    [Fact]
    public async Task GetSummariesAsync_ReturnsTheDistinctHostnamesReportingEachApplication()
    {
        await using var context = CreateContext();
        var hostA = new Host("host-a", "SERIAL-A");
        var hostB = new Host("host-b", "SERIAL-B");
        context.Hosts.AddRange(hostA, hostB);
        context.InstalledApplications.AddRange(
            new InstalledApplication(hostA.Id, "Firefox", "128.0"),
            new InstalledApplication(hostB.Id, "Firefox", "127.0"));
        await context.SaveChangesAsync();
        var repository = new InstalledApplicationRepository(context);

        var summaries = await repository.GetSummariesAsync(CancellationToken.None);

        Assert.Equal(new[] { "host-a", "host-b" }, Assert.Single(summaries).HostNames);
    }

    [Fact]
    public async Task GetSummariesAsync_NestsAChildApplicationUnderItsPackageManager()
    {
        await using var context = CreateContext();
        var host = new Host("host-a", "SERIAL-A");
        context.Hosts.Add(host);
        var homebrew = new InstalledApplication(host.Id, "Homebrew", "4.3.9");
        context.InstalledApplications.Add(homebrew);
        await context.SaveChangesAsync(); // homebrew needs an Id assigned before being referenced as a parent

        var firefox = new InstalledApplication(host.Id, "firefox", "128.0");
        firefox.SetParent(homebrew.Id);
        context.InstalledApplications.Add(firefox);
        await context.SaveChangesAsync();
        var repository = new InstalledApplicationRepository(context);

        var summaries = await repository.GetSummariesAsync(CancellationToken.None);

        var homebrewSummary = Assert.Single(summaries);
        Assert.Equal("Homebrew", homebrewSummary.Name);
        var child = Assert.Single(homebrewSummary.Children);
        Assert.Equal("firefox", child.Name);
        Assert.Equal(new[] { "host-a" }, child.HostNames);
    }

    [Fact]
    public async Task GetSummariesAsync_OrdersTopLevelApplicationsAlphabetically()
    {
        await using var context = CreateContext();
        var host = new Host("host-a", "SERIAL-A");
        context.Hosts.Add(host);
        context.InstalledApplications.AddRange(
            new InstalledApplication(host.Id, "Zed", "1.0"),
            new InstalledApplication(host.Id, "Alfred", "1.0"));
        await context.SaveChangesAsync();
        var repository = new InstalledApplicationRepository(context);

        var summaries = await repository.GetSummariesAsync(CancellationToken.None);

        Assert.Equal(new[] { "Alfred", "Zed" }, summaries.Select(s => s.Name));
    }

    [Fact]
    public async Task GetApplicationVersionVariantsAsync_DeduplicatesIdenticalCombinationsAcrossHosts()
    {
        await using var context = CreateContext();
        var hostA = new Host("host-a", "SERIAL-A", "macOS 15.0");
        var hostB = new Host("host-b", "SERIAL-B", "macOS 15.0");
        context.Hosts.AddRange(hostA, hostB);
        context.InstalledApplications.AddRange(
            new InstalledApplication(hostA.Id, "Firefox", "128.0"),
            new InstalledApplication(hostB.Id, "Firefox", "128.0")); // identical variant, different host
        await context.SaveChangesAsync();
        var repository = new InstalledApplicationRepository(context);

        var variants = await repository.GetApplicationVersionVariantsAsync(CancellationToken.None);

        Assert.Single(variants);
    }

    [Fact]
    public async Task GetApplicationVersionVariantsAsync_ResolvesTheParentApplicationsName()
    {
        await using var context = CreateContext();
        var host = new Host("host-a", "SERIAL-A", "macOS 15.0");
        context.Hosts.Add(host);
        var homebrew = new InstalledApplication(host.Id, "Homebrew", "4.3.9");
        context.InstalledApplications.Add(homebrew);
        await context.SaveChangesAsync();

        var firefox = new InstalledApplication(host.Id, "firefox", "128.0");
        firefox.SetParent(homebrew.Id);
        context.InstalledApplications.Add(firefox);
        await context.SaveChangesAsync();
        var repository = new InstalledApplicationRepository(context);

        var variants = await repository.GetApplicationVersionVariantsAsync(CancellationToken.None);

        var firefoxVariant = variants.Single(v => v.ApplicationName == "firefox");
        Assert.Equal("Homebrew", firefoxVariant.ParentApplicationName);
    }

    [Fact]
    public async Task GetByHostIdAsync_OnlyReturnsThatHostsApplications()
    {
        await using var context = CreateContext();
        var hostA = new Host("host-a", "SERIAL-A");
        var hostB = new Host("host-b", "SERIAL-B");
        context.Hosts.AddRange(hostA, hostB);
        context.InstalledApplications.AddRange(
            new InstalledApplication(hostA.Id, "Firefox", "128.0"),
            new InstalledApplication(hostB.Id, "Slack", "4.0.0"));
        await context.SaveChangesAsync();
        var repository = new InstalledApplicationRepository(context);

        var result = await repository.GetByHostIdAsync(hostA.Id, CancellationToken.None);

        Assert.Equal("Firefox", Assert.Single(result).Name);
    }
}
