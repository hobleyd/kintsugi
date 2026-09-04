using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Domain;

public class HostTests
{
    [Theory]
    [InlineData("", "SERIAL-1")]
    [InlineData(" ", "SERIAL-1")]
    [InlineData(null, "SERIAL-1")]
    public void Constructor_RejectsAMissingHostname(string? hostname, string serialNumber)
    {
        Assert.Throws<DomainException>(() => new Host(hostname!, serialNumber));
    }

    [Theory]
    [InlineData("host-1", "")]
    [InlineData("host-1", " ")]
    [InlineData("host-1", null)]
    public void Constructor_RejectsAMissingSerialNumber(string hostname, string? serialNumber)
    {
        Assert.Throws<DomainException>(() => new Host(hostname, serialNumber!));
    }

    [Fact]
    public void Constructor_SetsAllSuppliedFields()
    {
        var host = new Host("host-1", "SERIAL-1", "macOS 15.0", "10.0.0.5", operatingSystemUpdateAvailable: true, operatingSystemLatestVersion: "15.1");

        Assert.Equal("host-1", host.Hostname);
        Assert.Equal("SERIAL-1", host.SerialNumber);
        Assert.Equal("macOS 15.0", host.OperatingSystem);
        Assert.Equal("10.0.0.5", host.IpAddress);
        Assert.True(host.OperatingSystemUpdateAvailable);
        Assert.Equal("15.1", host.OperatingSystemLatestVersion);
        Assert.Equal(HostStatus.Unknown, host.Status);
    }

    [Fact]
    public void RecordHeartbeat_SetsStatusAndLastSeenUtc()
    {
        var host = new Host("host-1", "SERIAL-1");

        host.RecordHeartbeat(HostStatus.Online);

        Assert.Equal(HostStatus.Online, host.Status);
        Assert.NotNull(host.LastSeenUtc);
    }

    [Fact]
    public void Reregister_WithNullOptionalFields_LeavesExistingValuesUnchanged()
    {
        var host = new Host("host-1", "SERIAL-1", "macOS 14.0", "10.0.0.1", operatingSystemUpdateAvailable: true, operatingSystemLatestVersion: "15.0");

        host.Reregister("host-1-renamed", operatingSystem: null, ipAddress: null, operatingSystemUpdateAvailable: null, operatingSystemLatestVersion: null);

        Assert.Equal("host-1-renamed", host.Hostname); // hostname itself always updates
        Assert.Equal("macOS 14.0", host.OperatingSystem);
        Assert.Equal("10.0.0.1", host.IpAddress);
        Assert.True(host.OperatingSystemUpdateAvailable);
        Assert.Equal("15.0", host.OperatingSystemLatestVersion);
    }

    [Fact]
    public void Reregister_WithNewValues_OverwritesTheExistingOnes()
    {
        var host = new Host("host-1", "SERIAL-1", "macOS 14.0", "10.0.0.1");

        host.Reregister("host-1", "macOS 15.0", "10.0.0.2", operatingSystemUpdateAvailable: true, operatingSystemLatestVersion: "15.1");

        Assert.Equal("macOS 15.0", host.OperatingSystem);
        Assert.Equal("10.0.0.2", host.IpAddress);
        Assert.True(host.OperatingSystemUpdateAvailable);
        Assert.Equal("15.1", host.OperatingSystemLatestVersion);
    }

    [Fact]
    public void Reregister_WithExplicitFalseUpdateAvailable_OverwritesAStalePreviousTrue()
    {
        var host = new Host("host-1", "SERIAL-1", operatingSystemUpdateAvailable: true, operatingSystemLatestVersion: "15.1");

        host.Reregister("host-1", null, null, operatingSystemUpdateAvailable: false, operatingSystemLatestVersion: null);

        Assert.False(host.OperatingSystemUpdateAvailable);
    }

    [Fact]
    public void Reregister_ExplicitFalseUpdateAvailable_AlsoClearsAStaleLatestVersion()
    {
        // A definitive "nothing pending" should never leave a previously reported target version
        // (e.g. "15.1") still displayed once the host is already caught up.
        var host = new Host("host-1", "SERIAL-1", operatingSystemUpdateAvailable: true, operatingSystemLatestVersion: "15.1");

        host.Reregister("host-1", null, null, operatingSystemUpdateAvailable: false, operatingSystemLatestVersion: null);

        Assert.Null(host.OperatingSystemLatestVersion);
    }

    [Fact]
    public void Reregister_AlwaysRecordsAHeartbeatAsOnline()
    {
        var host = new Host("host-1", "SERIAL-1");

        host.Reregister("host-1", null, null);

        Assert.Equal(HostStatus.Online, host.Status);
        Assert.NotNull(host.LastSeenUtc);
    }

    [Fact]
    public void Reregister_RecordsTheReportedAgentVersion()
    {
        var host = new Host("host-1", "SERIAL-1", agentVersion: "0.6.0");

        host.Reregister("host-1", null, null, agentVersion: "0.6.1");

        Assert.Equal("0.6.1", host.AgentVersion);
    }

    [Fact]
    public void Reregister_WithNoAgentVersion_KeepsThePreviouslyReportedOne()
    {
        // An agent predating the field omits it; that must read as "not reported", not "none".
        var host = new Host("host-1", "SERIAL-1", agentVersion: "0.6.1");

        host.Reregister("host-1", null, null, agentVersion: null);

        Assert.Equal("0.6.1", host.AgentVersion);
    }

    [Fact]
    public void Reregister_RejectsAMissingHostname()
    {
        var host = new Host("host-1", "SERIAL-1");

        Assert.Throws<DomainException>(() => host.Reregister("", null, null));
    }

    [Fact]
    public void RecordOperatingSystemPatched_ClearsThePendingUpdateFlagAndTargetVersion()
    {
        var host = new Host("host-1", "SERIAL-1", operatingSystemUpdateAvailable: true, operatingSystemLatestVersion: "15.1");

        host.RecordOperatingSystemPatched();

        Assert.False(host.OperatingSystemUpdateAvailable);
        Assert.Null(host.OperatingSystemLatestVersion);
    }

    [Fact]
    public void RecordOperatingSystemPatched_AlsoRecordsAHeartbeatAsOnline()
    {
        var host = new Host("host-1", "SERIAL-1");

        host.RecordOperatingSystemPatched();

        Assert.Equal(HostStatus.Online, host.Status);
        Assert.NotNull(host.LastSeenUtc);
    }

    [Fact]
    public void RequestRemoval_SetsRemovalRequestedAndDeletedAtUtc()
    {
        var host = new Host("host-1", "SERIAL-1");

        host.RequestRemoval();

        Assert.True(host.RemovalRequested);
        Assert.NotNull(host.DeletedAtUtc);
    }

    [Fact]
    public void RequestRemoval_CalledTwice_LeavesTheOriginalDeletedAtUtcUnchanged()
    {
        var host = new Host("host-1", "SERIAL-1");

        host.RequestRemoval();
        var firstDeletedAtUtc = host.DeletedAtUtc;
        host.RequestRemoval();

        Assert.Equal(firstDeletedAtUtc, host.DeletedAtUtc);
    }
}
