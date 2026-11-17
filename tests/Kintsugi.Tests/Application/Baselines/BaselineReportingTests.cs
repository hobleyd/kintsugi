using Kintsugi.Application.Baselines;
using Kintsugi.Application.Baselines.Commands.AssignBaseline;
using Kintsugi.Application.Baselines.Commands.CreateBaseline;
using Kintsugi.Application.Baselines.Commands.CreateBaselineVersion;
using Kintsugi.Application.Baselines.Commands.ReportBaselineResults;
using Kintsugi.Application.Baselines.Commands.SignBaselineVersion;
using Kintsugi.Application.Baselines.Queries.GetAgentBaselines;
using Kintsugi.Application.Baselines.Queries.GetBaseline;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;
using Kintsugi.Infrastructure.Persistence;
using Kintsugi.Infrastructure.Persistence.Repositories;
using static Kintsugi.Tests.Application.Baselines.BaselineTestData;

namespace Kintsugi.Tests.Application.Baselines;

/// <summary>The agent's half end to end, over the real repositories: fetch, report only changes, and
/// the numbers the screen reads.</summary>
public class BaselineReportingTests : IAsyncLifetime
{
    private readonly ApplicationDbContext _context = Context();
    private BaselineRepository _baselines = default!;
    private HostRepository _hosts = default!;
    private readonly Host _windows = new("pc-1", "SERIAL-WIN", "Windows 11 Pro");
    private readonly Host _mac = new("mac-1", "SERIAL-MAC", "macOS 15.0");
    private Guid _baselineId;

    public async Task InitializeAsync()
    {
        _baselines = new BaselineRepository(_context);
        _hosts = new HostRepository(_context);
        await _hosts.AddAsync(_windows, CancellationToken.None);
        await _hosts.AddAsync(_mac, CancellationToken.None);
        await _context.SaveChangesAsync();

        _baselineId = (await CreateSignedVersion(null, Setting("copilot.off"), Setting("store.off", valueName: "Store"), Fact("edition"))).BaselineId;
        await new AssignBaselineCommandHandler(_baselines, _hosts, _context)
            .Handle(new AssignBaselineCommand(_baselineId, true, Array.Empty<string>()), CancellationToken.None);
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    private async Task<CreatedBaselineVersionDto> CreateSignedVersion(Guid? baselineId, params BaselineItem[] items)
    {
        var created = baselineId is null
            ? await new CreateBaselineCommandHandler(_baselines, _context).Handle(new CreateBaselineCommand("Windows workstation", items), CancellationToken.None)
            : await new CreateBaselineVersionCommandHandler(_baselines, _context).Handle(new CreateBaselineVersionCommand(baselineId.Value, items), CancellationToken.None);
        await new SignBaselineVersionCommandHandler(_baselines, SigningService(), _context)
            .Handle(new SignBaselineVersionCommand(created.VersionId, created.DocumentSha256), CancellationToken.None);
        return created;
    }

    private Task<AgentBaselinesDto> Fetch(string serial) =>
        new GetAgentBaselinesQueryHandler(_baselines, _hosts).Handle(new GetAgentBaselinesQuery(serial), CancellationToken.None);

    private Task Report(Guid versionId, ItemResultInput[] items, FactResultInput[]? facts = null) =>
        new ReportBaselineResultsCommandHandler(_baselines, _hosts, _context)
            .Handle(new ReportBaselineResultsCommand("SERIAL-WIN", new[] { new BaselineResultInput(versionId, items, facts ?? Array.Empty<FactResultInput>()) }), CancellationToken.None);

    [Fact]
    public async Task OnlyAssignedHostsAreServed_AndOnlySignedContent()
    {
        var served = Assert.Single((await Fetch("SERIAL-WIN")).Baselines);
        Assert.NotEmpty(served.Signature);
        Assert.Equal(served.VersionId, BaselineDocument.Parse(served.Document).VersionId);

        Assert.Empty((await Fetch("SERIAL-MAC")).Baselines);
        Assert.Empty((await Fetch("NOT-ENROLLED")).Baselines);

        // A newer draft does not displace the signed version.
        await new CreateBaselineVersionCommandHandler(_baselines, _context)
            .Handle(new CreateBaselineVersionCommand(_baselineId, new[] { Setting("copilot.off", "0") }), CancellationToken.None);
        Assert.Equal(served.VersionId, Assert.Single((await Fetch("SERIAL-WIN")).Baselines).VersionId);
    }

    [Fact]
    public async Task AReportIsRecorded_ServedBack_AndCounted()
    {
        var versionId = Assert.Single((await Fetch("SERIAL-WIN")).Baselines).VersionId;

        await Report(versionId,
            new[]
            {
                new ItemResultInput("copilot.off", "Compliant", "1", null),
                new ItemResultInput("store.off", "Drifted", null, null),
                // Not an item of this version: ignored rather than stored.
                new ItemResultInput("invented.key", "Compliant", "1", null),
            },
            new[] { new FactResultInput("edition", "Professional"), new FactResultInput("not-a-fact", "x") });

        var served = Assert.Single((await Fetch("SERIAL-WIN")).Baselines);
        Assert.Equal(new[] { ("copilot.off", "Compliant"), ("store.off", "Drifted") },
            served.States.OrderBy(s => s.Key).Select(s => (s.Key, s.State)));
        Assert.Equal("Professional", Assert.Single(served.Facts).Value);

        var detail = await new GetBaselineQueryHandler(_baselines, _hosts).Handle(new GetBaselineQuery(_baselineId), CancellationToken.None);
        Assert.Equal(new ComplianceCountsDto(1, 0, 0, 0, 0), detail.Items.Single(i => i.Item.Key == "copilot.off").Counts);
        Assert.Equal(new ComplianceCountsDto(0, 1, 0, 0, 0), detail.Items.Single(i => i.Item.Key == "store.off").Counts);
        Assert.Equal("pc-1", Assert.Single(detail.Hosts).Hostname);
    }

    [Fact]
    public async Task AnEmptyReport_ChangesNothingButLastChecked()
    {
        var versionId = Assert.Single((await Fetch("SERIAL-WIN")).Baselines).VersionId;
        await Report(versionId, new[] { new ItemResultInput("copilot.off", "Drifted", "0", null) });
        var before = (await _baselines.GetHostStatesAsync(_windows.Id, new[] { versionId }, CancellationToken.None)).Single();
        var changed = before.LastChangedUtc;

        await Task.Delay(5);
        await Report(versionId, Array.Empty<ItemResultInput>());

        var after = (await _baselines.GetHostStatesAsync(_windows.Id, new[] { versionId }, CancellationToken.None)).Single();
        Assert.Equal("0", after.ObservedValue);
        Assert.Equal(changed, after.LastChangedUtc);
        Assert.True(after.LastCheckedUtc > changed);
    }

    [Fact]
    public async Task AReportOnANewVersion_DropsTheHostsRowsForTheOldOne()
    {
        var v1 = Assert.Single((await Fetch("SERIAL-WIN")).Baselines).VersionId;
        await Report(v1, new[] { new ItemResultInput("copilot.off", "Compliant", "1", null) });

        var v2 = (await CreateSignedVersion(_baselineId, Setting("copilot.off"))).VersionId;
        Assert.Equal(v2, Assert.Single((await Fetch("SERIAL-WIN")).Baselines).VersionId);
        await Report(v2, new[] { new ItemResultInput("copilot.off", "Compliant", "1", null) });

        Assert.Empty(await _baselines.GetHostStatesAsync(_windows.Id, new[] { v1 }, CancellationToken.None));
        Assert.Single(await _baselines.GetHostStatesAsync(_windows.Id, new[] { v2 }, CancellationToken.None));
    }

    /// <summary>
    /// An agent whose fetch failed falls back to its cached copy (baseline::refresh) and reports on the
    /// version it holds. If that is an older one, recording it would delete the current version's rows.
    /// </summary>
    [Fact]
    public async Task AReportOnAnOlderVersion_IsDropped_AndTheCurrentRowsSurvive()
    {
        var v1 = Assert.Single((await Fetch("SERIAL-WIN")).Baselines).VersionId;
        var v2 = (await CreateSignedVersion(_baselineId, Setting("copilot.off"))).VersionId;
        await Report(v2, new[] { new ItemResultInput("copilot.off", "Drifted", "0", null) });

        await Report(v1, new[] { new ItemResultInput("copilot.off", "Compliant", "1", null) });

        var current = Assert.Single(await _baselines.GetHostStatesAsync(_windows.Id, new[] { v2 }, CancellationToken.None));
        Assert.Equal("0", current.ObservedValue);
        Assert.Empty(await _baselines.GetHostStatesAsync(_windows.Id, new[] { v1 }, CancellationToken.None));
    }

    [Fact]
    public async Task AHostListNamingANonWindowsHost_IsRefused_AndNothingIsAssigned()
    {
        var error = await Assert.ThrowsAsync<DomainException>(() => new AssignBaselineCommandHandler(_baselines, _hosts, _context)
            .Handle(new AssignBaselineCommand(_baselineId, false, new[] { "pc-1", "mac-1" }), CancellationToken.None));

        Assert.Contains("mac-1", error.Message);
        var baseline = (await _baselines.GetAsync(_baselineId, CancellationToken.None))!;
        Assert.True(baseline.AssignedToAllWindowsHosts);
        Assert.Empty(baseline.AssignedHostIds);
    }

    [Fact]
    public async Task AReportForABaselineThisHostIsNotAssigned_IsDropped()
    {
        var versionId = Assert.Single((await Fetch("SERIAL-WIN")).Baselines).VersionId;
        await new AssignBaselineCommandHandler(_baselines, _hosts, _context)
            .Handle(new AssignBaselineCommand(_baselineId, false, Array.Empty<string>()), CancellationToken.None);

        await Report(versionId, new[] { new ItemResultInput("copilot.off", "Compliant", "1", null) });

        Assert.Empty(await _baselines.GetHostStatesAsync(_windows.Id, new[] { versionId }, CancellationToken.None));
    }
}
