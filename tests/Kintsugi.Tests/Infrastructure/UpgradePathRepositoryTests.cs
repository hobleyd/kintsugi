using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Infrastructure.Persistence;
using Kintsugi.Infrastructure.Persistence.Repositories;

namespace Kintsugi.Tests.Infrastructure;

/// <summary>
/// Exercises UpgradePathRepository's query logic against EF Core's InMemory provider rather than
/// real PostgreSQL — fast and dependency-free, at the cost of not proving the LINQ actually
/// translates to valid SQL (the repository's own style of loading candidates then matching them in
/// plain C# largely sidesteps that risk anyway). A real-Postgres pass (e.g. via Testcontainers)
/// would close that gap if it's ever worth the added test-run weight.
/// </summary>
public class UpgradePathRepositoryTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    /// <summary>Where every Homebrew-managed row lives — see PlatformBucket.ForPackageManager.</summary>
    private static readonly string HomebrewBucket = PlatformBucket.ForPackageManager(PackageManagerCatalog.Homebrew);

    private static void AddHomebrewManagedApplication(ApplicationDbContext context, Guid hostId, string name, string version) =>
        AddManagedApplication(context, hostId, name, version, PackageManagerCatalog.Homebrew);

    /// <summary>
    /// Adds <paramref name="name"/> to a host alongside the package manager that manages it, linked
    /// as parent/child exactly the way RegisterApplicationsCommandHandler does — the link the
    /// repository's fallback lookup reads to know which manager's bucket to check.
    /// </summary>
    private static void AddManagedApplication(ApplicationDbContext context, Guid hostId, string name, string version, string packageManagerName)
    {
        var manager = new InstalledApplication(hostId, packageManagerName, "1.0");
        var managed = new InstalledApplication(hostId, name, version);
        managed.SetParent(manager.Id);
        context.InstalledApplications.AddRange(manager, managed);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNoMatchingRowExists()
    {
        await using var context = CreateContext();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetAsync("Firefox", "macOS", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_MatchesOnApplicationNameAndPlatformTogether()
    {
        await using var context = CreateContext();
        context.UpgradePaths.Add(UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script, null, null, null, null, null));
        context.UpgradePaths.Add(UpgradePath.Create("Firefox", "Windows", UpgradePathStatus.NotFound, null, UpgradeMethod.Unknown, null, null, null, null, null));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetAsync("Firefox", "macOS", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("128.0", result!.LatestVersion);
    }

    [Fact]
    public async Task GetAsync_MatchesApplicationNameCaseInsensitively()
    {
        // The scan planner groups installed-application variants case-insensitively
        // (PrepareUpgradePathScanQueryHandler), so the casing it settles on for a given application
        // can differ from what's already stored — an exact, case-sensitive match here would miss the
        // existing row and cause a fresh scan to insert a duplicate instead of updating it.
        await using var context = CreateContext();
        context.UpgradePaths.Add(UpgradePath.Create("Rectangle", PlatformBucket.Generic, UpgradePathStatus.Found, "1.0", UpgradeMethod.PackageManagerCommand, null, "brew upgrade rectangle", null, null, null));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetAsync("rectangle", PlatformBucket.Generic, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Rectangle", result!.ApplicationName);
    }

    [Fact]
    public async Task GetByApplicationIdentifierAsync_ReturnsTheMatchingRow()
    {
        await using var context = CreateContext();
        context.UpgradePaths.Add(UpgradePath.Create(
            "Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash", "org.mozilla.firefox"));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetByApplicationIdentifierAsync("org.mozilla.firefox", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Firefox", result!.ApplicationName);
    }

    [Fact]
    public async Task GetAllForApplicationAsync_ReturnsEveryRowForThatName_AcrossEveryPlatformItsStoredUnder()
    {
        await using var context = CreateContext();
        context.UpgradePaths.Add(UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.PackageManagerCommand, null, "brew upgrade Firefox", null, null, null));
        context.UpgradePaths.Add(UpgradePath.Create("Firefox", PlatformBucket.Generic, UpgradePathStatus.Found, "129.0", UpgradeMethod.Script, null, null, null, null, null, "#!/bin/bash\n..."));
        context.UpgradePaths.Add(UpgradePath.Create("Slack", "macOS", UpgradePathStatus.Found, "4.0.0", UpgradeMethod.Script, null, null, null, null, null));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetAllForApplicationAsync("firefox", CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal("Firefox", r.ApplicationName));
    }

    [Fact]
    public async Task Remove_DeletesTheRow_SoItNoLongerShadowsAReplacementUnderAnotherPlatform()
    {
        // Reproduces the exact cleanup RegisterApplicationsCommandHandler relies on: a legacy row
        // stored under the real OS platform must actually be gone, since GetSummariesAsync's
        // per-host platform lookup is tried before its Generic fallback and would otherwise keep
        // resolving to this stale row forever.
        await using var context = CreateContext();
        var legacyRow = UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.PackageManagerCommand, null, "brew upgrade Firefox", null, null, null);
        context.UpgradePaths.Add(legacyRow);
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        repository.Remove(legacyRow);
        await context.SaveChangesAsync();

        Assert.Null(await repository.GetAsync("Firefox", "macOS", CancellationToken.None));
    }

    [Fact]
    public async Task FindExistingSignatureForScriptAsync_ReturnsTheSignature_FromAnyOtherRowWithIdenticalScriptContent()
    {
        // Every Homebrew script is byte-identical across every application (see
        // HomebrewUpgradeScript.Build) — this is what lets Firefox's row inherit wget's signature.
        await using var context = CreateContext();
        var wget = UpgradePath.Create("wget", PlatformBucket.Generic, UpgradePathStatus.Found, "1.21", UpgradeMethod.Script, null, null, null, null, null, "#!/bin/bash\nshared\n");
        wget.SignScript("signed:#!/bin/bash\nshared\n");
        context.UpgradePaths.Add(wget);
        context.UpgradePaths.Add(UpgradePath.Create("Firefox", PlatformBucket.Generic, UpgradePathStatus.Found, "128.0", UpgradeMethod.Script, null, null, null, null, null, "#!/bin/bash\nshared\n"));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var signature = await repository.FindExistingSignatureForScriptAsync("#!/bin/bash\nshared\n", CancellationToken.None);

        Assert.Equal("signed:#!/bin/bash\nshared\n", signature);
    }

    [Fact]
    public async Task FindExistingSignatureForScriptAsync_ReturnsNull_WhenNoRowWithThatScriptIsSigned()
    {
        await using var context = CreateContext();
        context.UpgradePaths.Add(UpgradePath.Create("Firefox", PlatformBucket.Generic, UpgradePathStatus.Found, "128.0", UpgradeMethod.Script, null, null, null, null, null, "#!/bin/bash\nshared\n"));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var signature = await repository.FindExistingSignatureForScriptAsync("#!/bin/bash\nshared\n", CancellationToken.None);

        Assert.Null(signature);
    }

    [Fact]
    public async Task GetUnsignedRowsWithScriptAsync_ReturnsEveryUnsignedRowSharingThatExactScript()
    {
        await using var context = CreateContext();
        var signedAlready = UpgradePath.Create("Slack", PlatformBucket.Generic, UpgradePathStatus.Found, "4.0.0", UpgradeMethod.Script, null, null, null, null, null, "#!/bin/bash\nshared\n");
        signedAlready.SignScript("signed:#!/bin/bash\nshared\n");
        context.UpgradePaths.Add(signedAlready);
        context.UpgradePaths.Add(UpgradePath.Create("Firefox", PlatformBucket.Generic, UpgradePathStatus.Found, "128.0", UpgradeMethod.Script, null, null, null, null, null, "#!/bin/bash\nshared\n"));
        context.UpgradePaths.Add(UpgradePath.Create("wget", PlatformBucket.Generic, UpgradePathStatus.Found, "1.21", UpgradeMethod.Script, null, null, null, null, null, "#!/bin/bash\nshared\n"));
        context.UpgradePaths.Add(UpgradePath.Create("Zoom", PlatformBucket.Generic, UpgradePathStatus.Found, "6.0.0", UpgradeMethod.Script, null, null, null, null, null, "#!/bin/bash\nsomething-else\n"));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetUnsignedRowsWithScriptAsync("#!/bin/bash\nshared\n", CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.ApplicationName == "Firefox");
        Assert.Contains(result, r => r.ApplicationName == "wget");
    }

    [Fact]
    public async Task GetStatusesAsync_IsScopedToOneHostBySerialNumber()
    {
        await using var context = CreateContext();
        var thisHost = new Host("host-1", "SERIAL-1", "macOS 15.0");
        var otherHost = new Host("host-2", "SERIAL-2", "macOS 15.0");
        context.Hosts.AddRange(thisHost, otherHost);
        context.InstalledApplications.Add(new InstalledApplication(thisHost.Id, "Firefox", "128.0"));
        context.InstalledApplications.Add(new InstalledApplication(otherHost.Id, "Slack", "4.0.0"));
        context.UpgradePaths.Add(UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.Found, "129.0", UpgradeMethod.Script, null, null, null, null, null));
        context.UpgradePaths.Add(UpgradePath.Create("Slack", "macOS", UpgradePathStatus.Found, "4.1.0", UpgradeMethod.Script, null, null, null, null, null));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetStatusesAsync("SERIAL-1", CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Firefox", result[0].ApplicationName);
    }

    [Fact]
    public async Task GetStatusesAsync_ReportsUpdateAvailable_WhenTheLatestVersionIsNewer()
    {
        await using var context = CreateContext();
        var host = new Host("host-1", "SERIAL-1", "macOS 15.0");
        context.Hosts.Add(host);
        context.InstalledApplications.Add(new InstalledApplication(host.Id, "Firefox", "128.0"));
        context.UpgradePaths.Add(UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.Found, "129.0", UpgradeMethod.Script, null, null, null, null, null));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetStatusesAsync("SERIAL-1", CancellationToken.None);

        Assert.True(result.Single().UpdateAvailable);
    }

    [Fact]
    public async Task GetStatusesAsync_FallsBackToThePackageManagersEntry_WhenNoPlatformSpecificOneExists()
    {
        await using var context = CreateContext();
        var host = new Host("host-1", "SERIAL-1", "macOS 15.0");
        context.Hosts.Add(host);
        AddHomebrewManagedApplication(context, host.Id, "SomeCli", "1.0.0");
        // A Homebrew-managed application's upgrade path lives under Homebrew's own bucket rather
        // than any OS one, so it's this fallback — not the (name, "macOS") lookup — that has to find it.
        context.UpgradePaths.Add(UpgradePath.Create(
            "SomeCli", HomebrewBucket, UpgradePathStatus.Found, "1.1.0", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n..."));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetStatusesAsync("SERIAL-1", CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("1.1.0", result[0].LatestVersion);
    }

    [Fact]
    public async Task GetStatusesAsync_NeverHandsAWindowsHost_APackageManagerRowThatManagerDoesNotManage()
    {
        // The regression the per-manager bucket exists to prevent: a Windows host with an
        // application whose name matches a Homebrew formula used to fall back onto the shared
        // "generic" bucket and be handed a signed `#!/bin/bash` script — which its agent, seeing a
        // genuine signature, would have run.
        await using var context = CreateContext();
        var windowsHost = new Host("pc-1", "SERIAL-PC", "Windows 11 Pro");
        context.Hosts.Add(windowsHost);
        // Installed standalone on Windows — no package manager of its own.
        context.InstalledApplications.Add(new InstalledApplication(windowsHost.Id, "wget", "1.21"));
        context.UpgradePaths.Add(UpgradePath.Create(
            "wget", HomebrewBucket, UpgradePathStatus.Found, "1.24", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\nbrew update && brew upgrade \"$APP_NAME\"\n"));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetStatusesAsync("SERIAL-PC", CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetStatusesAsync_ExcludesAnInstalledApplicationWithNoUpgradePathAtAll()
    {
        await using var context = CreateContext();
        var host = new Host("host-1", "SERIAL-1", "macOS 15.0");
        context.Hosts.Add(host);
        context.InstalledApplications.Add(new InstalledApplication(host.Id, "NeverResearched", "1.0.0"));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetStatusesAsync("SERIAL-1", CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetStatusesAsync_OrdersUpdatesAvailableFirst_ThenAlphabeticallyByApplicationName()
    {
        await using var context = CreateContext();
        var host = new Host("host-1", "SERIAL-1", "macOS 15.0");
        context.Hosts.Add(host);
        context.InstalledApplications.AddRange(
            new InstalledApplication(host.Id, "Zed", "1.0.0"),
            new InstalledApplication(host.Id, "AlreadyCurrent", "2.0.0"),
            new InstalledApplication(host.Id, "Firefox", "128.0"));
        context.UpgradePaths.AddRange(
            UpgradePath.Create("Zed", "macOS", UpgradePathStatus.Found, "1.1.0", UpgradeMethod.Script, null, null, null, null, null),
            UpgradePath.Create("AlreadyCurrent", "macOS", UpgradePathStatus.Found, "2.0.0", UpgradeMethod.Script, null, null, null, null, null),
            UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.Found, "129.0", UpgradeMethod.Script, null, null, null, null, null));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetStatusesAsync("SERIAL-1", CancellationToken.None);

        Assert.Equal(new[] { "Firefox", "Zed", "AlreadyCurrent" }, result.Select(r => r.ApplicationName));
    }

    [Fact]
    public async Task GetSummariesAsync_AggregatesHostCountsAcrossMultipleHostsOnTheSameVersion()
    {
        await using var context = CreateContext();
        var hostA = new Host("host-a", "SERIAL-A", "macOS 15.0");
        var hostB = new Host("host-b", "SERIAL-B", "macOS 15.0");
        context.Hosts.AddRange(hostA, hostB);
        context.InstalledApplications.AddRange(
            new InstalledApplication(hostA.Id, "Firefox", "128.0"),
            new InstalledApplication(hostB.Id, "Firefox", "128.0"));
        context.UpgradePaths.Add(UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.Found, "129.0", UpgradeMethod.Script, null, null, null, null, null));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetSummariesAsync(CancellationToken.None);

        var summary = Assert.Single(result);
        Assert.Equal(2, summary.HostCount);
        Assert.Equal(0, summary.UpToDateHostCount);
        Assert.Equal(2, summary.UpdateAvailableHostCount);
    }

    [Fact]
    public async Task GetSummariesAsync_SplitsHostCountsByVersion_WhenHostsAreOnDifferentVersions()
    {
        await using var context = CreateContext();
        var upToDateHost = new Host("host-a", "SERIAL-A", "macOS 15.0");
        var behindHost = new Host("host-b", "SERIAL-B", "macOS 15.0");
        context.Hosts.AddRange(upToDateHost, behindHost);
        context.InstalledApplications.AddRange(
            new InstalledApplication(upToDateHost.Id, "Firefox", "129.0"),
            new InstalledApplication(behindHost.Id, "Firefox", "128.0"));
        context.UpgradePaths.Add(UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.Found, "129.0", UpgradeMethod.Script, null, null, null, null, null));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var summary = (await repository.GetSummariesAsync(CancellationToken.None)).Single();

        Assert.Equal(2, summary.HostCount);
        Assert.Equal(1, summary.UpToDateHostCount);
        Assert.Equal(1, summary.UpdateAvailableHostCount);
        Assert.Equal(new[] { "host-b" }, summary.HostNamesNeedingUpdate);
    }

    [Fact]
    public async Task GetSummariesAsync_WithNoKnownLatestVersion_ReportsZeroForBothUpToDateAndUpdateAvailable()
    {
        // No LatestVersion means nothing concrete is known yet — report "unknown" (0/0) rather
        // than guessing that every host is either up to date or behind.
        await using var context = CreateContext();
        var host = new Host("host-1", "SERIAL-1", "macOS 15.0");
        context.Hosts.Add(host);
        context.InstalledApplications.Add(new InstalledApplication(host.Id, "Zoom", "6.0.0"));
        context.UpgradePaths.Add(UpgradePath.Create("Zoom", "macOS", UpgradePathStatus.NotFound, null, UpgradeMethod.Unknown, null, null, null, null, null));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var summary = (await repository.GetSummariesAsync(CancellationToken.None)).Single();

        Assert.Equal(1, summary.HostCount);
        Assert.Equal(0, summary.UpToDateHostCount);
        Assert.Equal(0, summary.UpdateAvailableHostCount);
        Assert.Empty(summary.HostNamesNeedingUpdate);
    }

    [Fact]
    public async Task GetSummariesAsync_SplitsTheSameApplicationNameByPlatform()
    {
        await using var context = CreateContext();
        var macHost = new Host("mac-host", "SERIAL-MAC", "macOS 15.0");
        var winHost = new Host("win-host", "SERIAL-WIN", "Windows 11");
        context.Hosts.AddRange(macHost, winHost);
        context.InstalledApplications.AddRange(
            new InstalledApplication(macHost.Id, "Chrome", "128.0"),
            new InstalledApplication(winHost.Id, "Chrome", "128.0"));
        context.UpgradePaths.AddRange(
            UpgradePath.Create("Chrome", "macOS", UpgradePathStatus.Found, "129.0", UpgradeMethod.Script, null, null, null, null, null),
            UpgradePath.Create("Chrome", "Windows", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script, null, null, null, null, null));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var results = await repository.GetSummariesAsync(CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(1, results.Single(r => r.Platform == "macOS").UpdateAvailableHostCount);
        Assert.Equal(1, results.Single(r => r.Platform == "Windows").UpToDateHostCount);
    }

    [Fact]
    public async Task GetSummariesAsync_WhenTwoRowsExistForTheSameApplicationDifferingOnlyByCasing_DoesNotThrow_AndReportsTheMostRecentlyCheckedOne()
    {
        // Reproduces a real production crash: the DB's uniqueness constraint on
        // (ApplicationName, Platform) is case-sensitive, so "Rectangle"/generic and
        // "rectangle"/generic can both exist at once (e.g. a scan settled on a different casing
        // than what was already stored — see GetAsync's case-insensitive match, added to stop this
        // from recurring). Building the by-name-and-platform lookup must tolerate that rather than
        // throwing "An item with the same key has already been added" on every page load.
        await using var context = CreateContext();
        var host = new Host("mac-host", "SERIAL-MAC", "macOS 15.0");
        context.Hosts.Add(host);
        AddHomebrewManagedApplication(context, host.Id, "rectangle", "1.0.0");

        // The stale row is created (and CheckedUtc stamped) first; a real time gap before creating
        // the fresh one — rather than a fabricated timestamp — is what makes "most recently checked"
        // deterministic here, since UpgradePath.CheckedUtc always stamps DateTimeOffset.UtcNow and
        // has no public way to set it to an arbitrary value.
        context.UpgradePaths.Add(UpgradePath.Create("Rectangle", HomebrewBucket, UpgradePathStatus.Found, "0.9.0", UpgradeMethod.PackageManagerCommand, null, "brew upgrade rectangle", null, null, null));
        await context.SaveChangesAsync();
        await Task.Delay(10);
        context.UpgradePaths.Add(UpgradePath.Create("rectangle", HomebrewBucket, UpgradePathStatus.Found, "1.0.0", UpgradeMethod.Script, null, null, null, null, null, "#!/bin/bash\n..."));
        await context.SaveChangesAsync();

        var repository = new UpgradePathRepository(context);

        var summary = (await repository.GetSummariesAsync(CancellationToken.None)).Single();

        Assert.Equal(UpgradeMethod.Script, summary.Method);
        Assert.Equal("1.0.0", summary.LatestVersion);
    }

    [Fact]
    public async Task GetSummariesAsync_ForAPackageManagerPath_ReportsTheStoredManagerBucket_NotTheInstalledHostsOsBucket()
    {
        // The row is persisted under its manager's bucket regardless of which OS bucket the
        // installed hosts fall into (see PrepareUpgradePathScanQueryHandler). The Applications page
        // round-trips this value back to the API (e.g. the per-row instructions panel) to look the
        // item back up by (name, platform) — reporting the installed hosts' OS bucket here instead
        // of the row's real key means that round trip can never find it.
        await using var context = CreateContext();
        var host = new Host("mac-host", "SERIAL-MAC", "macOS 15.0");
        context.Hosts.Add(host);
        AddHomebrewManagedApplication(context, host.Id, "firefox", "128.0");
        context.UpgradePaths.Add(UpgradePath.Create(
            "firefox", HomebrewBucket, UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n..."));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var summary = (await repository.GetSummariesAsync(CancellationToken.None)).Single(s => s.ApplicationName == "firefox");

        Assert.Equal(HomebrewBucket, summary.Platform);
    }

    [Fact]
    public async Task GetSummariesAsync_ForOneApplicationManagedByTwoDifferentManagers_ReportsARowPerManager()
    {
        // A Mac installing VLC from Homebrew and a PC installing it from winget are two genuinely
        // different upgrade mechanisms with two different scripts — grouping by the host's OS bucket
        // alone used to collapse them into one row, which meant one of the two hosts was shown (and
        // handed) the wrong manager's upgrade path.
        await using var context = CreateContext();
        var mac = new Host("mac-host", "SERIAL-MAC", "macOS 15.0");
        var pc = new Host("pc-1", "SERIAL-PC", "Windows 11 Pro");
        context.Hosts.AddRange(mac, pc);
        AddHomebrewManagedApplication(context, mac.Id, "vlc", "3.0.20");
        AddManagedApplication(context, pc.Id, "vlc", "3.0.20", PackageManagerCatalog.Winget);
        context.UpgradePaths.AddRange(
            UpgradePath.Create("vlc", HomebrewBucket, UpgradePathStatus.Found, "3.0.21", UpgradeMethod.Script, null, null, null, null, null, "#!/bin/bash\n..."),
            UpgradePath.Create("vlc", PlatformBucket.ForPackageManager(PackageManagerCatalog.Winget), UpgradePathStatus.Found, "3.0.21", UpgradeMethod.Script, null, null, null, null, null, "winget...\n"));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var summaries = (await repository.GetSummariesAsync(CancellationToken.None)).Where(s => s.ApplicationName == "vlc").ToList();

        Assert.Equal(2, summaries.Count);
        Assert.Contains(summaries, s => s.Platform == HomebrewBucket);
        Assert.Contains(summaries, s => s.Platform == PlatformBucket.ForPackageManager(PackageManagerCatalog.Winget));
    }

    [Fact]
    public async Task GetAppUpdateCountsByHostAsync_ReturnsAnEmptyDictionary_WhenNoInstalledApplicationHasAKnownUpgradePath()
    {
        await using var context = CreateContext();
        var host = new Host("host-1", "SERIAL-1", "macOS 15.0");
        context.Hosts.Add(host);
        context.InstalledApplications.Add(new InstalledApplication(host.Id, "NeverResearched", "1.0.0"));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetAppUpdateCountsByHostAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAppUpdateCountsByHostAsync_CountsEachOutdatedApplicationOnceForItsHost()
    {
        await using var context = CreateContext();
        var host = new Host("host-1", "SERIAL-1", "macOS 15.0");
        context.Hosts.Add(host);
        context.InstalledApplications.AddRange(
            new InstalledApplication(host.Id, "Firefox", "128.0"),
            new InstalledApplication(host.Id, "Zoom", "6.0.0"));
        context.UpgradePaths.AddRange(
            UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.Found, "129.0", UpgradeMethod.Script, null, null, null, null, null),
            UpgradePath.Create("Zoom", "macOS", UpgradePathStatus.Found, "6.1.0", UpgradeMethod.Script, null, null, null, null, null));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetAppUpdateCountsByHostAsync(CancellationToken.None);

        Assert.Equal(2, result[host.Id]);
    }

    [Fact]
    public async Task GetAppUpdateCountsByHostAsync_ExcludesAnApplicationThatIsAlreadyUpToDate()
    {
        await using var context = CreateContext();
        var host = new Host("host-1", "SERIAL-1", "macOS 15.0");
        context.Hosts.Add(host);
        context.InstalledApplications.AddRange(
            new InstalledApplication(host.Id, "Firefox", "129.0"),
            new InstalledApplication(host.Id, "Zoom", "6.0.0"));
        context.UpgradePaths.AddRange(
            UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.Found, "129.0", UpgradeMethod.Script, null, null, null, null, null),
            UpgradePath.Create("Zoom", "macOS", UpgradePathStatus.Found, "6.1.0", UpgradeMethod.Script, null, null, null, null, null));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetAppUpdateCountsByHostAsync(CancellationToken.None);

        Assert.Equal(1, result[host.Id]);
    }

    [Fact]
    public async Task GetAppUpdateCountsByHostAsync_WithNoKnownLatestVersion_DoesNotCountTheApplication()
    {
        await using var context = CreateContext();
        var host = new Host("host-1", "SERIAL-1", "macOS 15.0");
        context.Hosts.Add(host);
        context.InstalledApplications.Add(new InstalledApplication(host.Id, "Zoom", "6.0.0"));
        context.UpgradePaths.Add(UpgradePath.Create("Zoom", "macOS", UpgradePathStatus.NotFound, null, UpgradeMethod.Unknown, null, null, null, null, null));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetAppUpdateCountsByHostAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAppUpdateCountsByHostAsync_FallsBackToThePackageManagersEntry_WhenNoPlatformSpecificOneExists()
    {
        await using var context = CreateContext();
        var host = new Host("host-1", "SERIAL-1", "macOS 15.0");
        context.Hosts.Add(host);
        AddHomebrewManagedApplication(context, host.Id, "SomeCli", "1.0.0");
        context.UpgradePaths.Add(UpgradePath.Create(
            "SomeCli", HomebrewBucket, UpgradePathStatus.Found, "1.1.0", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n..."));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetAppUpdateCountsByHostAsync(CancellationToken.None);

        Assert.Equal(1, result[host.Id]);
    }

    [Fact]
    public async Task GetAppUpdateCountsByHostAsync_KeepsCountsSeparateAcrossHosts()
    {
        await using var context = CreateContext();
        var hostWithUpdate = new Host("host-1", "SERIAL-1", "macOS 15.0");
        var hostUpToDate = new Host("host-2", "SERIAL-2", "macOS 15.0");
        context.Hosts.AddRange(hostWithUpdate, hostUpToDate);
        context.InstalledApplications.AddRange(
            new InstalledApplication(hostWithUpdate.Id, "Firefox", "128.0"),
            new InstalledApplication(hostUpToDate.Id, "Firefox", "129.0"));
        context.UpgradePaths.Add(UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.Found, "129.0", UpgradeMethod.Script, null, null, null, null, null));
        await context.SaveChangesAsync();
        var repository = new UpgradePathRepository(context);

        var result = await repository.GetAppUpdateCountsByHostAsync(CancellationToken.None);

        Assert.Equal(1, result[hostWithUpdate.Id]);
        Assert.False(result.ContainsKey(hostUpToDate.Id));
    }
}
