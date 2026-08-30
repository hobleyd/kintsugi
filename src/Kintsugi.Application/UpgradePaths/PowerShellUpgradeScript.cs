namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// The parts every server-written PowerShell upgrade script shares — the CLI contract's argument
/// parsing, a version comparator, and a redirect reader. Kept in one place so
/// <see cref="WingetUpgradeScript"/> and <see cref="ChocolateyUpgradeScript"/> can't drift apart on
/// the contract itself, which the agent (<c>upgrade.rs</c>'s <c>patch_one</c>), the server
/// (<c>CheckScriptVersionAsync</c>), and the AI-authored scripts all depend on being identical.
/// </summary>
/// <remarks>
/// Every snippet here is ASCII-only on purpose. A script with no byte-order mark is decoded by
/// Windows PowerShell 5.1 using the system ANSI code page, not UTF-8, so a stray em dash in a
/// comment would arrive mangled on a managed host. The agent writes its temp copy with a BOM to
/// close that gap for AI-authored scripts too, but there's no reason for the scripts this system
/// writes itself to depend on that.
/// </remarks>
public static class PowerShellUpgradeScript
{
    /// <summary>
    /// Parses <c>--appName &lt;name&gt; --appId &lt;id&gt; (--update-version|--update)</c> out of
    /// <c>$args</c> into <c>$AppName</c>, <c>$AppId</c>, and <c>$Mode</c>. Written against
    /// <c>$args</c> rather than a <c>param()</c> block on purpose: PowerShell's own parameter
    /// binding can't express double-dashed names, and the contract is fixed by what the agent
    /// already sends.
    /// </summary>
    public const string ArgumentParsing = """
        function Show-Usage {
            [Console]::Error.WriteLine('Usage: script.ps1 --appName <name> --appId <id> (--update-version|--update)')
            exit 1
        }

        $AppName = ''
        $AppId = ''
        $Mode = ''
        for ($i = 0; $i -lt $args.Count; $i++) {
            switch ($args[$i]) {
                '--appName' { if ($i + 1 -ge $args.Count) { Show-Usage }; $AppName = $args[++$i] }
                '--appId'   { if ($i + 1 -ge $args.Count) { Show-Usage }; $AppId = $args[++$i] }
                '--update-version' { if ($Mode) { Show-Usage }; $Mode = 'update-version' }
                '--update'  { if ($Mode) { Show-Usage }; $Mode = 'update' }
                default { Show-Usage }
            }
        }
        if (-not $AppName -or -not $AppId -or -not $Mode) { Show-Usage }
        """;

    /// <summary>
    /// Turns a version string into something <c>Sort-Object</c> orders correctly — zero-padding
    /// each numeric run to a fixed width, so "1.10.0" sorts after "1.9.0" instead of before it the
    /// way a plain string comparison would. A leading "v" is dropped so a release tag compares the
    /// same as a bare version.
    /// </summary>
    public const string VersionSortHelper = """
        function ConvertTo-SortableVersion {
            param([string] $Version)
            $normalized = $Version.TrimStart('v', 'V')
            return ([regex]::Replace($normalized, '\d+', { param($m) $m.Value.PadLeft(10, '0') }))
        }
        """;

    /// <summary>
    /// Reads a single redirect's <c>Location</c> header without following it — how a
    /// <c>releases/latest</c> URL yields the newest tag with no API call, and so no rate limit.
    /// </summary>
    /// <remarks>
    /// Uses <c>HttpClientHandler</c> directly rather than
    /// <c>Invoke-WebRequest -MaximumRedirection 0</c>: in PowerShell 7 that parameter makes a
    /// redirect *throw* rather than return the response, so the header is unreachable — which is
    /// exactly how the first version of this failed when run for real.
    /// </remarks>
    public const string RedirectLocationHelper = """
        function Get-RedirectLocation {
            param([string] $Uri)
            # System.Net.Http is loaded by default in PowerShell 7 (where --update-version runs on
            # the server) but not in the Windows PowerShell 5.1 this script also declares support
            # for, where the types below would otherwise be "unable to find type" errors.
            Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue
            $handler = [System.Net.Http.HttpClientHandler]::new()
            $handler.AllowAutoRedirect = $false
            $client = [System.Net.Http.HttpClient]::new($handler)
            try {
                $response = $client.GetAsync($Uri).GetAwaiter().GetResult()
                $location = $response.Headers.Location
                if (-not $location) { throw "no redirect returned by $Uri" }
                return [string] $location
            } finally {
                $client.Dispose()
                $handler.Dispose()
            }
        }
        """;
}
