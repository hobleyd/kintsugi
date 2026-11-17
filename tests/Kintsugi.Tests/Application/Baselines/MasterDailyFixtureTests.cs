using System.Text.Json;
using Kintsugi.Application.Baselines.Commands.CreateBaseline;
using Kintsugi.WebApi.Controllers;

namespace Kintsugi.Tests.Application.Baselines;

/// <summary>
/// <c>baselines/master-daily.json</c> is pasted into the Baselines screen as-is, so it has to be exactly
/// what <c>POST /api/admin/baselines</c> accepts: Master Daily sections 1–12 and 17, machine scope only, audit.
/// </summary>
public class MasterDailyFixtureTests
{
    private static CreateBaselineRequest Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kintsugi.sln")))
        {
            directory = directory.Parent;
        }

        var json = File.ReadAllText(Path.Combine(directory!.FullName, "baselines", "master-daily.json"));
        return JsonSerializer.Deserialize<CreateBaselineRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    [Fact]
    public void HoldsMasterDailys34MachineSettings_InAudit_AndPassesTheCreateValidator()
    {
        var fixture = Load();

        Assert.Equal(34, fixture.Items.Count);
        Assert.All(fixture.Items, i =>
        {
            Assert.Equal("setting", i.Kind);
            Assert.Equal("machine", i.Scope);
            Assert.Equal("audit", i.Mode);
        });

        var result = new CreateBaselineCommandValidator().Validate(new CreateBaselineCommand(fixture.Name, fixture.Items));
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    /// <summary>Section 2's token is a secret: the fixture names it and carries no value.</summary>
    [Fact]
    public void TheChromeTokenNamesASecretAndCarriesNoValue()
    {
        var secret = Assert.Single(Load().Items, i => i.Secret is not null);
        Assert.Equal("CloudManagementEnrollmentToken", secret.ValueName);
        Assert.Null(secret.Data);
    }
}
