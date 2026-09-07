using Kintsugi.Application.AgentPackages;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Tests.Application.AgentPackages;

/// <summary>
/// The CrowdStrike deployment script. Nothing here runs PowerShell — these assert the properties
/// that make the script safe to hand to a fleet, each of which fails silently if it regresses: the
/// pin is present and is the upstream hash rather than the stored one, the download is the URL the
/// pin was taken over, verification happens before extraction, and every rendered value is quoted
/// so a token or a URL cannot become something PowerShell evaluates.
/// </summary>
public class WindowsBootstrapScriptTests
{
    private const string ApiBaseUrl = "https://patch.internal:8443";
    private const string UpstreamSha256 = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string StoredSha256 = "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03";
    private const string DownloadUrl =
        "https://github.com/hobleyd/kintsugi/releases/download/windows-agent-v0.7.4/kintsugi-agent-windows-0.7.4.tar.gz";

    private static AgentPackage Package(
        string? upstreamSha256 = UpstreamSha256,
        string? downloadUrl = DownloadUrl) =>
        AgentPackage.Create(
            "windows", "0.7.4", "kintsugi-agent-windows-0.7.4.tar.gz", 8_000_000,
            StoredSha256, "signature", "Release notes.", upstreamSha256, downloadUrl);

    [Fact]
    public void Build_PinsTheUpstreamChecksum_NotTheStoredArchivesOwn()
    {
        // The two are different by construction: the stored archive has had its api_base_url
        // rewritten, and the script downloads from GitHub, which has not. Pinning the stored one
        // would fail every install — or, worse, be "fixed" by someone relaxing the check.
        var script = WindowsBootstrapScript.Build(Package(), ApiBaseUrl, "token");

        Assert.Contains($"$ExpectedSha256  = '{UpstreamSha256}'", script);
        Assert.DoesNotContain(StoredSha256, script);
    }

    [Fact]
    public void Build_DownloadsFromTheUrlThePinWasTakenOver()
    {
        var script = WindowsBootstrapScript.Build(Package(), ApiBaseUrl, "token");

        Assert.Contains($"$DownloadUrl     = '{DownloadUrl}'", script);
    }

    [Fact]
    public void Build_VerifiesBeforeItExtracts()
    {
        // Ordering is the property, not the presence of a Get-FileHash call: extracting first and
        // checking afterwards would run the installer out of an unverified archive.
        var script = WindowsBootstrapScript.Build(Package(), ApiBaseUrl, "token");

        var verify = script.IndexOf("Get-FileHash", StringComparison.Ordinal);
        var extract = script.IndexOf("tar.exe -xzf", StringComparison.Ordinal);

        Assert.True(verify > 0, "the script no longer hashes what it downloaded");
        Assert.True(extract > 0, "the script no longer extracts the archive");
        Assert.True(verify < extract, "the archive is extracted before its checksum is verified");
    }

    [Fact]
    public void Build_MismatchIsFatal()
    {
        var script = WindowsBootstrapScript.Build(Package(), ApiBaseUrl, "token");

        // `throw` rather than a warning: an archive that does not match the pin is precisely the
        // case this whole path exists for, and installing it anyway would make the check theatre.
        var mismatch = script.IndexOf("-ine $ExpectedSha256", StringComparison.Ordinal);
        Assert.True(mismatch > 0, "the script no longer compares the download against the pin");
        Assert.Contains("throw", script[mismatch..(mismatch + 600)]);
    }

    [Fact]
    public void Build_PointsTheAgentAtThisServer()
    {
        // The GitHub archive ships the kintsugi.example.com placeholder, and a host left on it
        // enrolls against nothing. See AdminClientsController.ResolveAgentApiBaseUrl.
        var script = WindowsBootstrapScript.Build(Package(), ApiBaseUrl, "token");

        Assert.Contains($"$ApiBaseUrl      = '{ApiBaseUrl}'", script);
        Assert.Contains("api_base_url", script);
    }

    [Fact]
    public void Build_CarriesTheCurrentEnrollmentToken()
    {
        var script = WindowsBootstrapScript.Build(Package(), ApiBaseUrl, "s3cret-token");

        Assert.Contains("$EnrollmentToken = 's3cret-token'", script);
    }

    [Fact]
    public void Build_NoEnrollmentToken_RendersABlankOneRatherThanFailing()
    {
        // install.ps1 handles a blank token with a legible warning and the agent then fails
        // enrollment with a named reason. That is a better outcome than refusing to render a script
        // at all on a server whose token has not been set yet.
        var script = WindowsBootstrapScript.Build(Package(), ApiBaseUrl, null);

        Assert.Contains("$EnrollmentToken = ''", script);
    }

    [Theory]
    [InlineData("it's-a-token", "'it''s-a-token'")]
    [InlineData("$(rm -rf /)", "'$(rm -rf /)'")]
    [InlineData("a`b", "'a`b'")]
    public void Build_RenderedValuesAreInertPowerShellLiterals(string token, string expected)
    {
        // Single-quoted, so PowerShell expands nothing inside them, and an apostrophe is doubled.
        // The token is a secret whose content nothing here controls — the same reasoning install.ps1
        // applies where it writes one into config.toml.
        var script = WindowsBootstrapScript.Build(Package(), ApiBaseUrl, token);

        Assert.Contains($"$EnrollmentToken = {expected}", script);
    }

    [Fact]
    public void Build_RestrictsItsWorkingDirectoryBeforeExtracting()
    {
        // Running as SYSTEM puts the temp directory in C:\Windows\Temp, which any authenticated
        // user may write to. Verifying and then extracting somewhere writable leaves a window in
        // which the verified bytes and the executed ones are not the same file.
        var script = WindowsBootstrapScript.Build(Package(), ApiBaseUrl, "token");

        var restrict = script.IndexOf("icacls.exe $WorkDir /inheritance:r", StringComparison.Ordinal);
        var extract = script.IndexOf("tar.exe -xzf", StringComparison.Ordinal);

        Assert.True(restrict > 0, "the working directory is no longer locked down");
        Assert.True(restrict < extract, "the archive is extracted before the working directory is locked down");
        // SIDs, not localized names — the same rule install.ps1 follows for the queue directory.
        Assert.Contains("*S-1-5-18", script);
        Assert.Contains("*S-1-5-32-544", script);
    }

    [Fact]
    public void Build_PinsTheSchemeAcrossRedirects()
    {
        // GitHub redirects release downloads to its asset host, so a scheme pinned only on the
        // first request proves nothing about where the bytes came from.
        var script = WindowsBootstrapScript.Build(Package(), ApiBaseUrl, "token");

        Assert.Contains("--proto '=https'", script);
        Assert.Contains("--proto-redir '=https'", script);
    }

    [Fact]
    public void Build_DelegatesToThePackagedInstaller()
    {
        // Not reimplemented: install.ps1 owns `obj= LocalSystem`, the queue ACL by SID and the
        // BOM-less config write, each of which fails quietly when a second copy drifts.
        var script = WindowsBootstrapScript.Build(Package(), ApiBaseUrl, "token");

        Assert.Contains("install.ps1", script);
        Assert.Contains("& powershell.exe @installerArgs", script);
    }

    [Fact]
    public void Build_IsAsciiOnly()
    {
        // Windows PowerShell 5.1 decodes a BOM-less .ps1 using the system ANSI code page rather than
        // UTF-8, and this script is one an operator saves or pastes themselves, so nothing controls
        // the encoding it lands in. Every other server-written script in this repo is kept ASCII for
        // the same reason. An em dash in a comment is harmless until the mojibake it decodes to
        // happens to contain a quote or a backtick.
        var script = WindowsBootstrapScript.Build(Package(), ApiBaseUrl, "token");

        var offending = script.Where(c => c > '\u007f').Distinct().ToArray();
        Assert.True(offending.Length == 0, $"non-ASCII characters in the script: {string.Join(", ", offending)}");
    }

    [Fact]
    public void Build_WithoutUpstreamProvenance_Throws()
    {
        // Callers report NoUpstreamProvenanceReason instead. Rendering a script with an empty pin
        // is the one outcome that would be worse than rendering none.
        Assert.Throws<InvalidOperationException>(
            () => WindowsBootstrapScript.Build(Package(upstreamSha256: null), ApiBaseUrl, "token"));
    }

    [Fact]
    public void AgentPackage_HalfAPin_IsNoPin()
    {
        // A hash with no address, or an address with nothing to check it against, is a script that
        // would download without verifying.
        Assert.Null(Package(downloadUrl: null).UpstreamSha256);
        Assert.Null(Package(upstreamSha256: null).UpstreamDownloadUrl);
    }

    [Fact]
    public void AgentPackage_RecordUpstreamProvenance_FillsAGapButNeverOverwritesOne()
    {
        var withoutPin = Package(upstreamSha256: null, downloadUrl: null);
        Assert.True(withoutPin.RecordUpstreamProvenance(UpstreamSha256, DownloadUrl));
        Assert.Equal(UpstreamSha256, withoutPin.UpstreamSha256);

        // A published version's bytes never change (see PublishAgentPackageCommandHandler), so
        // neither may the pin over them.
        Assert.False(withoutPin.RecordUpstreamProvenance(StoredSha256, "https://elsewhere.example/x.tar.gz"));
        Assert.Equal(UpstreamSha256, withoutPin.UpstreamSha256);
        Assert.Equal(DownloadUrl, withoutPin.UpstreamDownloadUrl);
    }
}
