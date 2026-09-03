using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.Vanta;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.Vanta;

public class VantaResourceBuilderTests
{
    private static readonly VantaSettingsSnapshot Settings = new(
        Enabled: true,
        ClientId: "client",
        ClientSecret: "secret",
        ApiBaseUrl: "https://api.vanta.com",
        VulnerableComponentResourceId: "vc-1",
        PackageVulnerabilityResourceId: "pv-1",
        ConsoleBaseUrl: "https://kintsugi.example.com",
        Severity: 5.0d,
        SyncIntervalHours: 24);

    private static Host CheckedInHost(
        string hostname = "mac-01",
        string serial = "C02ABC123",
        string? operatingSystem = "macOS 14.5",
        bool? osUpdateAvailable = false,
        string? osLatestVersion = null)
    {
        var host = new Host(hostname, serial, operatingSystem, "10.0.0.1", osUpdateAvailable, osLatestVersion);
        host.RecordHeartbeat(HostStatus.Online);
        return host;
    }

    private static UpgradeStatusDto OutdatedApp(
        string application = "Firefox",
        string hostname = "mac-01",
        string serial = "C02ABC123",
        string installed = "120.0",
        string? latest = "121.0",
        UpgradeMethod method = UpgradeMethod.Script,
        string? script = "#!/bin/bash",
        string? scriptSignature = "sig",
        string? applicationIdentifier = "org.mozilla.firefox",
        string? instructions = null,
        string? command = null,
        string? commandSignature = null) =>
        new(
            application, hostname, serial, installed, latest, true,
            UpgradePathStatus.Found, method, null, command, instructions, null, null,
            DateTimeOffset.UtcNow, script, applicationIdentifier, scriptSignature, commandSignature);

    [Fact]
    public void Build_KeysAComponentOnTheSerialNumberAndAPackageOnSerialPlusApplicationName()
    {
        var host = CheckedInHost();

        var snapshot = VantaResourceBuilder.Build(new[] { host }, new[] { OutdatedApp() }, Settings);

        // Derived ids, never row identity. InstalledApplication rows are deleted and recreated on
        // every inventory report (RegisterApplicationsCommandHandler), so a row-keyed id would change
        // every check-in — and since each sync replaces the whole state of the world, Vanta would see
        // every vulnerability in the fleet deleted and recreated daily.
        Assert.Equal("kintsugi:host:c02abc123", snapshot.Components.Single().UniqueId);
        Assert.Equal("kintsugi:app:c02abc123:firefox", snapshot.Packages.Single().UniqueId);
        Assert.Equal(snapshot.Components.Single().UniqueId, snapshot.Packages.Single().VulnerableComponentUniqueId);
    }

    [Fact]
    public void Build_IsStableAcrossTwoBuildsOfTheSameFleetState()
    {
        var hosts = new[] { CheckedInHost(serial: "B"), CheckedInHost(hostname: "mac-02", serial: "A") };
        var outdated = new[]
        {
            OutdatedApp(application: "Zed", serial: "A", hostname: "mac-02"),
            OutdatedApp(application: "Firefox", serial: "A", hostname: "mac-02"),
        };

        var first = VantaResourceBuilder.Build(hosts, outdated, Settings);
        var second = VantaResourceBuilder.Build(hosts, outdated, Settings);

        // Ordering is what makes a diff of two sync runs meaningful, so it must not depend on the
        // order the repositories happened to return rows in.
        Assert.Equal(first.Components.Select(c => c.UniqueId), second.Components.Select(c => c.UniqueId));
        Assert.Equal(first.Packages.Select(p => p.UniqueId), second.Packages.Select(p => p.UniqueId));
        Assert.Equal(new[] { "kintsugi:host:a", "kintsugi:host:b" }, first.Components.Select(c => c.UniqueId));
        Assert.Equal(
            new[] { "kintsugi:app:a:firefox", "kintsugi:app:a:zed" },
            first.Packages.Select(p => p.UniqueId));
    }

    [Fact]
    public void Build_StampsTheComponentWithWhenTheHostLastCheckedIn()
    {
        var host = CheckedInHost();

        var snapshot = VantaResourceBuilder.Build(new[] { host }, Array.Empty<UpgradeStatusDto>(), Settings);

        // Not "now". A host that stopped checking in three weeks ago must not look freshly scanned —
        // the timestamp is what Vanta reads as when this data was collected from the machine.
        Assert.Equal(host.LastSeenUtc, snapshot.Components.Single().CollectedTimestamp);
    }

    [Fact]
    public void Build_SkipsAHostThatHasNeverCheckedIn()
    {
        var neverSeen = new Host("mac-99", "NEVER", "macOS 14.5");

        var snapshot = VantaResourceBuilder.Build(
            new[] { neverSeen }, new[] { OutdatedApp(serial: "NEVER") }, Settings);

        // collectedTimestamp is required and nothing has ever been collected from this host. Its
        // applications go too — a package naming a component Vanta does not hold is an orphan.
        Assert.Empty(snapshot.Components);
        Assert.Empty(snapshot.Packages);
    }

    [Fact]
    public void Build_ReportsAPendingOperatingSystemUpdateAsItsOwnPackage()
    {
        var host = CheckedInHost(osUpdateAvailable: true, osLatestVersion: "15.1");

        var snapshot = VantaResourceBuilder.Build(new[] { host }, Array.Empty<UpgradeStatusDto>(), Settings);

        var package = snapshot.Packages.Single();
        Assert.Equal("kintsugi:os:c02abc123", package.UniqueId);
        Assert.Equal("Operating system", package.PackageName);
        Assert.Equal("macOS 14.5", package.PackageVersion);
        Assert.Contains("15.1", package.Description);
        // Every agent installs OS updates through its privileged half, so this is always actionable.
        Assert.True(package.IsResolvable);
    }

    [Fact]
    public void Build_OmitsTheOperatingSystemPackageWhenNoUpdateIsPending()
    {
        var snapshot = VantaResourceBuilder.Build(
            new[] { CheckedInHost(osUpdateAvailable: false) }, Array.Empty<UpgradeStatusDto>(), Settings);

        Assert.Empty(snapshot.Packages);
    }

    [Fact]
    public void Build_MarksAScriptRowResolvableOnlyWhenTheAgentCouldActuallyRunIt()
    {
        var host = new[] { CheckedInHost() };

        var signed = VantaResourceBuilder.Build(host, new[] { OutdatedApp() }, Settings);
        var unsigned = VantaResourceBuilder.Build(host, new[] { OutdatedApp(scriptSignature: null) }, Settings);
        var noIdentifier = VantaResourceBuilder.Build(host, new[] { OutdatedApp(applicationIdentifier: null) }, Settings);

        // Mirrors the agents' own is_patchable: an unsigned script is one no agent will run, and a
        // Script row needs an applicationIdentifier to be invoked at all.
        Assert.True(signed.Packages.Single().IsResolvable);
        Assert.False(unsigned.Packages.Single().IsResolvable);
        Assert.False(noIdentifier.Packages.Single().IsResolvable);
    }

    [Fact]
    public void Build_MarksAPackageManagerCommandRowResolvableOnlyWhenTheCommandIsSigned()
    {
        var host = new[] { CheckedInHost() };
        var signed = OutdatedApp(
            method: UpgradeMethod.PackageManagerCommand, script: null, scriptSignature: null,
            command: "brew upgrade firefox", commandSignature: "sig");
        var unsigned = signed with { CommandSignature = null };

        Assert.True(VantaResourceBuilder.Build(host, new[] { signed }, Settings).Packages.Single().IsResolvable);
        Assert.False(VantaResourceBuilder.Build(host, new[] { unsigned }, Settings).Packages.Single().IsResolvable);
    }

    [Fact]
    public void Build_PrefersResearchedInstructionsThenTheCommandThenAPlainSentence()
    {
        var host = new[] { CheckedInHost() };

        var withInstructions = VantaResourceBuilder.Build(
            host, new[] { OutdatedApp(instructions: "  Download the DMG.  ") }, Settings);
        var withCommand = VantaResourceBuilder.Build(
            host, new[] { OutdatedApp(command: "brew upgrade firefox") }, Settings);
        var withNeither = VantaResourceBuilder.Build(host, new[] { OutdatedApp() }, Settings);

        Assert.Equal("Download the DMG.", withInstructions.Packages.Single().RemediationInstructions);
        Assert.Equal("Run: brew upgrade firefox", withCommand.Packages.Single().RemediationInstructions);
        // Vanta requires non-empty remediation text, so the fallback still says something true.
        Assert.Equal(
            "Upgrade Firefox from 120.0 to 121.0 on mac-01.",
            withNeither.Packages.Single().RemediationInstructions);
    }

    [Fact]
    public void Build_CarriesTheConfiguredSeverityAndSaysWhereItCameFrom()
    {
        var snapshot = VantaResourceBuilder.Build(
            new[] { CheckedInHost() },
            new[] { OutdatedApp() },
            Settings with { Severity = 7.5d });

        var package = snapshot.Packages.Single();
        Assert.Equal(7.5d, package.Severity);
        // The severity is a configured constant, not a measurement — this system has no CVE feed, and
        // the record says so rather than letting a reader assume one.
        Assert.Contains("not from a CVE feed", package.Description);
    }

    [Fact]
    public void VantaPackageVulnerability_HasNoCveOrCvssFieldsAtAll()
    {
        var names = typeof(VantaPackageVulnerability).GetProperties().Select(p => p.Name).ToList();

        // Absent rather than nullable, deliberately: Kintsugi compares versions, so any value here
        // would be a guess presented as a finding in a compliance record.
        Assert.DoesNotContain(names, n => n.StartsWith("Cve", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.StartsWith("Cvss", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Equals("IsReachable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_DropsAnOutdatedApplicationWhoseHostIsNotBeingSynced()
    {
        var snapshot = VantaResourceBuilder.Build(
            new[] { CheckedInHost(serial: "KEPT") },
            new[] { OutdatedApp(serial: "KEPT"), OutdatedApp(application: "Zed", serial: "GONE") },
            Settings);

        // Every package names its component by uniqueId, so one whose host is absent would be an
        // orphan on Vanta's side.
        Assert.Equal("kintsugi:app:kept:firefox", Assert.Single(snapshot.Packages).UniqueId);
    }

    [Fact]
    public void Build_LinksEveryRecordBackIntoThisServersOwnAdminUi()
    {
        var snapshot = VantaResourceBuilder.Build(new[] { CheckedInHost() }, new[] { OutdatedApp() }, Settings);

        Assert.Equal("https://kintsugi.example.com/hosts", snapshot.Components.Single().ExternalUrl);
        // The same deep link the Hosts screen's own "N app updates" badge uses.
        Assert.Equal(
            "https://kintsugi.example.com/applications?status=update-available&host=mac-01",
            snapshot.Packages.Single().ExternalUrl);
    }

    [Fact]
    public void Build_ReportsEveryComponentAsAHostRatherThanGuessingAMachineRole()
    {
        var snapshot = VantaResourceBuilder.Build(
            new[] { CheckedInHost(), CheckedInHost(hostname: "srv-01", serial: "S1", operatingSystem: "Debian GNU/Linux 12 (Linux)") },
            Array.Empty<UpgradeStatusDto>(),
            Settings);

        // Kintsugi records an operating system, not a role. Mapping Linux to SERVER and macOS to
        // WORKSTATION would file evidence under a category this system cannot actually determine.
        Assert.All(snapshot.Components, c => Assert.Equal("HOST", c.TargetType));
    }
}
