using System.Text.Json;
using Kintsugi.Application.Vanta;
using Kintsugi.Infrastructure.Vanta;

namespace Kintsugi.Tests.Infrastructure;

/// <summary>
/// Asserts what actually goes on the wire, which no other test covers: the builder tests check what
/// lands in the records, and a renamed property between the two would pass every one of them and
/// surface only as Vanta rejecting the sync.
/// </summary>
/// <remarks>
/// The expected names are the required lists from <c>PUT /v1/resources/vulnerable_component</c> and
/// <c>PUT /v1/resources/package_vulnerability_connectors</c> in
/// https://developer.vanta.com/reference/build-integrations.json, transcribed by hand — which is the
/// same hand-mirroring the DTOs themselves are, so this catches drift on our side of the contract
/// rather than Vanta's.
/// </remarks>
public class VantaSyncClientSerializationTests
{
    private static readonly VantaVulnerableComponent Component = new(
        "mac-01", "kintsugi:host:c02abc123", "https://kintsugi.example.com/hosts",
        DateTimeOffset.Parse("2026-09-03T04:05:06Z"), "mac-01", "macOS 14.5 host.", "HOST");

    private static readonly VantaPackageVulnerability Package = new(
        "Firefox 120.0 on mac-01", "kintsugi:app:c02abc123:firefox",
        "https://kintsugi.example.com/applications", "Firefox", "120.0", 5.0d,
        "kintsugi:host:c02abc123", "Out of date.", true, "Upgrade it.");

    private static JsonElement Serialize(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, VantaSyncClient.JsonOptions)).RootElement;

    [Fact]
    public void AVulnerableComponent_CarriesEveryFieldTheSchemaRequires()
    {
        var json = Serialize(Component);

        foreach (var name in new[]
                 {
                     "displayName", "uniqueId", "externalUrl", "collectedTimestamp",
                     "name", "description", "targetType",
                 })
        {
            Assert.True(json.TryGetProperty(name, out _), $"missing required property '{name}'");
        }
    }

    [Fact]
    public void APackageVulnerability_CarriesEveryFieldTheSchemaRequires()
    {
        var json = Serialize(Package);

        foreach (var name in new[]
                 {
                     "displayName", "uniqueId", "externalUrl", "packageName", "packageVersion",
                     "severity", "vulnerableComponentUniqueId", "description", "isResolvable",
                     "remediationInstructions",
                 })
        {
            Assert.True(json.TryGetProperty(name, out _), $"missing required property '{name}'");
        }
    }

    [Fact]
    public void APackageVulnerability_PutsNoCveOrCvssFieldOnTheWireAtAll()
    {
        var json = Serialize(Package);

        // Not even as an explicit null. Kintsugi compares versions and has no CVE feed, so any value
        // here would be a guess arriving in a compliance record as a finding.
        foreach (var name in new[] { "cveId", "cvss3Score", "cvss3Vector", "isReachable" })
        {
            Assert.False(json.TryGetProperty(name, out _), $"'{name}' must never be sent");
        }
    }

    [Fact]
    public void ACollectedTimestamp_IsWrittenAsAnIso8601Instant()
    {
        var json = Serialize(Component);

        // The spec types this as date-time; System.Text.Json's default for DateTimeOffset is
        // ISO-8601, and this is the assertion that notices if that ever stops being true.
        var raw = json.GetProperty("collectedTimestamp").GetString();
        Assert.NotNull(raw);
        Assert.Equal(DateTimeOffset.Parse("2026-09-03T04:05:06Z"), DateTimeOffset.Parse(raw!));
    }

    [Fact]
    public void ASyncBody_WrapsTheResourcesUnderResourceId()
    {
        // The shape every sync endpoint takes: { resourceId, resources: [...] }. Built inline in
        // VantaSyncClient.SyncAsync as an anonymous type, so this is what pins its property names.
        var json = Serialize(new { resourceId = "vc-1", resources = new[] { Component } });

        Assert.Equal("vc-1", json.GetProperty("resourceId").GetString());
        Assert.Equal(1, json.GetProperty("resources").GetArrayLength());
        Assert.Equal(
            "kintsugi:host:c02abc123",
            json.GetProperty("resources")[0].GetProperty("uniqueId").GetString());
    }
}
