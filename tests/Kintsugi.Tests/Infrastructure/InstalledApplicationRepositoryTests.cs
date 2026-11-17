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

    // Agents report an application's name as the platform spells it, so one product arrives as
    // "Slack" from a Mac and "slack" from a Linux package. Every other name comparison in the
    // upgrade-path code already ignores case; this list used to be the one place that did not, and
    // showed a product twice, each row with part of its hosts.
    [Fact]
    public async Task GetSummariesAsync_MergesNamesThatDifferOnlyInCase_IntoOneRow()
    {
        await using var context = CreateContext();
        var hostA = new Host("host-a", "SERIAL-A");
        var hostB = new Host("host-b", "SERIAL-B");
        context.Hosts.AddRange(hostA, hostB);
        context.InstalledApplications.AddRange(
            new InstalledApplication(hostA.Id, "Slack", "4.38"),
            new InstalledApplication(hostB.Id, "slack", "4.38"));
        await context.SaveChangesAsync();
        var repository = new InstalledApplicationRepository(context);

        var summaries = await repository.GetSummariesAsync(CancellationToken.None);

        var slack = Assert.Single(summaries);
        Assert.Equal(2, slack.HostCount);
        Assert.Equal(new[] { "host-a", "host-b" }, slack.HostNames);
    }

    [Fact]
    public async Task GetSummariesAsync_ShowsTheSpellingMostHostsReport()
    {
        await using var context = CreateContext();
        var hosts = Enumerable.Range(1, 4).Select(i => new Host($"host-{i}", $"SERIAL-{i}")).ToList();
        context.Hosts.AddRange(hosts);
        context.InstalledApplications.AddRange(
            new InstalledApplication(hosts[0].Id, "Slack", "4.38"),
            new InstalledApplication(hosts[1].Id, "slack", "4.38"),
            new InstalledApplication(hosts[2].Id, "slack", "4.38"),
            new InstalledApplication(hosts[3].Id, "slack", "4.38"));
        await context.SaveChangesAsync();
        var repository = new InstalledApplicationRepository(context);

        var summaries = await repository.GetSummariesAsync(CancellationToken.None);

        Assert.Equal("slack", Assert.Single(summaries).Name);
    }

    [Fact]
    public async Task GetSummariesAsync_BreaksATieBetweenSpellingsTheSameWayEveryTime()
    {
        await using var context = CreateContext();
        var hostA = new Host("host-a", "SERIAL-A");
        var hostB = new Host("host-b", "SERIAL-B");
        context.Hosts.AddRange(hostA, hostB);
        context.InstalledApplications.AddRange(
            new InstalledApplication(hostA.Id, "slack", "4.38"),
            new InstalledApplication(hostB.Id, "Slack", "4.38"));
        await context.SaveChangesAsync();
        var repository = new InstalledApplicationRepository(context);

        var first = await repository.GetSummariesAsync(CancellationToken.None);
        var second = await repository.GetSummariesAsync(CancellationToken.None);

        // Capitalised first: it is the spelling a person wrote, and ordinal order puts it ahead.
        Assert.Equal("Slack", Assert.Single(first).Name);
        Assert.Equal("Slack", Assert.Single(second).Name);
    }

    [Fact]
    public async Task GetSummariesAsync_PrefersAMixedCaseSpelling_WhenSpellingsTie()
    {
        await using var context = CreateContext();
        var hosts = Enumerable.Range(1, 3).Select(i => new Host($"host-{i}", $"SERIAL-{i}")).ToList();
        context.Hosts.AddRange(hosts);
        context.InstalledApplications.AddRange(
            new InstalledApplication(hosts[0].Id, "slack", "4.38"),
            new InstalledApplication(hosts[1].Id, "SLACK", "4.38"),
            new InstalledApplication(hosts[2].Id, "Slack", "4.38"));
        await context.SaveChangesAsync();
        var repository = new InstalledApplicationRepository(context);

        var summaries = await repository.GetSummariesAsync(CancellationToken.None);

        var slack = Assert.Single(summaries);
        Assert.Equal("Slack", slack.Name);
        Assert.Equal(3, slack.HostCount);
    }

    [Fact]
    public async Task GetSummariesAsync_CountsAHostOnce_WhenItHasReportedTwoSpellings()
    {
        await using var context = CreateContext();
        var host = new Host("host-a", "SERIAL-A");
        context.Hosts.Add(host);
        context.InstalledApplications.AddRange(
            new InstalledApplication(host.Id, "Slack", "4.38"),
            new InstalledApplication(host.Id, "slack", "4.37")); // a stale row from an older agent
        await context.SaveChangesAsync();
        var repository = new InstalledApplicationRepository(context);

        var summaries = await repository.GetSummariesAsync(CancellationToken.None);

        var slack = Assert.Single(summaries);
        Assert.Equal(1, slack.HostCount);
        Assert.Equal(new[] { "host-a" }, slack.HostNames);
    }

    [Fact]
    public async Task GetSummariesAsync_NestsAChildUnderItsPackageManager_WhateverCaseTheManagerIsWrittenIn()
    {
        await using var context = CreateContext();
        var hostA = new Host("host-a", "SERIAL-A");
        var hostB = new Host("host-b", "SERIAL-B");
        context.Hosts.AddRange(hostA, hostB);
        var homebrewA = new InstalledApplication(hostA.Id, "Homebrew", "4.3.9");
        var homebrewB = new InstalledApplication(hostB.Id, "homebrew", "4.3.9");
        context.InstalledApplications.AddRange(homebrewA, homebrewB);
        await context.SaveChangesAsync();

        var firefoxA = new InstalledApplication(hostA.Id, "firefox", "128.0");
        firefoxA.SetParent(homebrewA.Id);
        var firefoxB = new InstalledApplication(hostB.Id, "Firefox", "128.0");
        firefoxB.SetParent(homebrewB.Id);
        context.InstalledApplications.AddRange(firefoxA, firefoxB);
        await context.SaveChangesAsync();
        var repository = new InstalledApplicationRepository(context);

        var summaries = await repository.GetSummariesAsync(CancellationToken.None);

        var homebrew = Assert.Single(summaries);
        Assert.Equal(2, homebrew.HostCount);
        var firefox = Assert.Single(homebrew.Children);
        Assert.Equal(2, firefox.HostCount);
    }

    [Fact]
    public async Task GetSummariesAsync_LeavesDifferentApplicationsApart()
    {
        await using var context = CreateContext();
        var host = new Host("host-a", "SERIAL-A");
        context.Hosts.Add(host);
        context.InstalledApplications.AddRange(
            new InstalledApplication(host.Id, "Slack", "4.38"),
            new InstalledApplication(host.Id, "Slack Helper", "4.38"));
        await context.SaveChangesAsync();
        var repository = new InstalledApplicationRepository(context);

        var summaries = await repository.GetSummariesAsync(CancellationToken.None);

        Assert.Equal(new[] { "Slack", "Slack Helper" }, summaries.Select(s => s.Name));
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
