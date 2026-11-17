using System.Text.RegularExpressions;
using Kintsugi.Application.Baselines;
using static Kintsugi.Tests.Application.Baselines.BaselineTestData;

namespace Kintsugi.Tests.Application.Baselines;

/// <summary>
/// <c>enforce</c> is accepted only for a value on <see cref="BaselineItemRules.EnforceableValues"/>, and
/// that list is the same one the Windows agent compiles in (<c>baseline::ENFORCEABLE</c>).
/// </summary>
public class BaselineEnforceTests
{
    private static BaselineItem ChromeOcclusion(string mode = "enforce") =>
        Setting("chrome.window-occlusion-off", data: "0", valueName: "WindowOcclusionEnabled")
            with { Path = @"SOFTWARE\Policies\Google\Chrome", Mode = mode };

    [Fact]
    public void Validator_AcceptsEnforce_ForAValueOnTheAllowlist_InAnyCase()
    {
        Assert.True(new BaselineItemValidator().Validate(ChromeOcclusion()).IsValid);
        Assert.True(new BaselineItemValidator().Validate(ChromeOcclusion() with { Path = @"software\policies\google\chrome", ValueName = "windowocclusionenabled" }).IsValid);
        Assert.True(new BaselineItemValidator().Validate(ChromeOcclusion("audit")).IsValid);
    }

    /// <summary>Each of these is a SYSTEM write that runs code or weakens the machine; none is on the list.</summary>
    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Updater", "string")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\Spooler", "ImagePath", "expandString")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\sethc.exe", "Debugger", "string")]
    [InlineData(@"SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist", "1", "string")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "WUServer", "string")]
    [InlineData(@"SOFTWARE\Policies\Google\Chrome", "WindowOcclusionEnabled", "string")]
    public void Validator_RefusesEnforce_ForAnythingElse(string path, string valueName, string valueType)
    {
        var item = Setting("x", data: "1", valueName: valueName) with { Path = path, ValueType = valueType, Mode = "enforce" };

        var result = new BaselineItemValidator().Validate(item);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("EnforceableValues"));
        Assert.True(new BaselineItemValidator().Validate(item with { Mode = "audit" }).IsValid);
    }

    [Fact]
    public void Validator_RefusesEnforce_ForASecret_AndAnyModeButAuditOrEnforce()
    {
        Assert.False(new BaselineItemValidator().Validate(ChromeOcclusion() with { Data = null, Secret = "OCCLUSION" }).IsValid);
        Assert.False(new BaselineItemValidator().Validate(ChromeOcclusion("set")).IsValid);
    }

    /// <summary>
    /// The agent's list is the one that actually stops a write, so it must never be wider than this one,
    /// nor narrower (a value the server lets you enforce that the agent then refuses only drifts).
    /// </summary>
    [Fact]
    public void TheAgentsAllowlist_IsExactlyTheServers()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kintsugi.sln")))
        {
            directory = directory.Parent;
        }

        var source = File.ReadAllText(Path.Combine(directory!.FullName, "clients", "windows-agent", "src", "baseline.rs"));
        var block = Regex.Match(source, @"const ENFORCEABLE: \[\(&str, &str, &str\); \d+\] = \[(?<entries>.*?)\];", RegexOptions.Singleline);
        Assert.True(block.Success, "baseline.rs no longer declares ENFORCEABLE in the shape this test reads");

        var agent = Regex.Matches(block.Groups["entries"].Value, @"\(r""(?<path>[^""]+)"", ""(?<name>[^""]+)"", ""(?<type>[^""]+)""\)")
            .Select(m => new EnforceableValue(m.Groups["path"].Value, m.Groups["name"].Value, m.Groups["type"].Value))
            .ToHashSet();

        Assert.NotEmpty(agent);
        Assert.Equal(BaselineItemRules.EnforceableValues.ToHashSet(), agent);
    }
}
