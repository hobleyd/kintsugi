namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// The fixed --appName/--appId/--update-version/--update script for everything Chocolatey manages,
/// Chocolatey itself included — the second recognized Windows package manager alongside
/// <see cref="WingetUpgradeScript"/>. Never AI-generated.
/// </summary>
/// <remarks>
/// <para>
/// <c>--update-version</c> queries the Chocolatey Community Repository's OData v2 feed, which is a
/// plain HTTPS GET with no key and no rate limit worth designing around, so it runs happily on the
/// (Linux) API server under <c>pwsh</c>; <c>--update</c> shells out to <c>choco</c> on the managed
/// Windows host. Chocolatey addresses a package by its package id, which is what the Windows agent
/// reports as a Chocolatey entry's <c>applicationIdentifier</c> — so, as with every other
/// server-written script, the id is read from <c>--appId</c> at runtime rather than baked in,
/// keeping <see cref="Build"/>'s output byte-identical across every package.
/// </para>
/// <para>
/// Like <see cref="SnapUpgradeScript"/>, this is one text for Chocolatey's own row and every package
/// it manages with no branch between them, and for the same reason: <c>chocolatey</c> is itself an
/// ordinary package on the same feed, and the
/// Windows agent reports Chocolatey's own row with that id (<c>system_info::scan_chocolatey</c>), so
/// <c>choco upgrade chocolatey</c> is the same operation as <c>choco upgrade firefox</c> with a
/// different <c>--appId</c>. The manager's row and every package it manages therefore share one
/// script and one signature — which is what the Applications screen relies on when it shows the
/// manager's script once on behalf of the packages nested under it (see
/// <see cref="HomebrewUpgradeScript"/>). This script used to hard-code the id for the manager's row;
/// it no longer needs to.
/// </para>
/// </remarks>
public static class ChocolateyUpgradeScript
{
    /// <summary>Chocolatey requires elevation for anything that writes to its install root. The
    /// Windows agent runs <c>--update</c> from its SYSTEM service rather than the per-user tray
    /// process for exactly this reason — see <c>clients/windows-agent/src/queue.rs</c>.</summary>
    public static string Build()
    {
        return $$"""
            #Requires -Version 5.1
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'

            {{PowerShellUpgradeScript.ArgumentParsing}}

            function Get-LatestVersion {
                param([string] $PackageId)
                # OData v2 (the NuGet protocol Chocolatey's community feed speaks): filtering on
                # IsLatestVersion asks the feed itself which release is current, rather than pulling
                # every version down and sorting them here.
                #
                # tolower(Id), not Id: the feed's `eq` is case-sensitive, and a package's canonical
                # id is often capitalised while what gets reported and passed around here is not.
                # Comparing raw returned an empty feed for exactly those packages -- which, under
                # Set-StrictMode, surfaced as a confusing "property 'entry' cannot be found".
                $encodedId = [uri]::EscapeDataString($PackageId.ToLowerInvariant())
                $uri = "https://community.chocolatey.org/api/v2/Packages()?`$filter=tolower(Id) eq '$encodedId' and IsLatestVersion"
                $response = Invoke-WebRequest -Uri $uri -Headers @{ 'User-Agent' = 'kintsugi-agent' } -ErrorAction Stop

                # Walked as child nodes rather than via $xml.feed.entry: under Set-StrictMode,
                # reading a property that isn't there at all (an empty feed) throws instead of
                # returning $null, which turns "no such package" into an unrelated-looking error.
                $xml = [xml] $response.Content
                $entries = @($xml.feed.ChildNodes | Where-Object { $_.LocalName -eq 'entry' })
                if ($entries.Count -eq 0) { throw "no published version found for $PackageId" }

                $version = $entries[0].properties.Version
                if (-not $version) { throw "the feed returned no version for $PackageId" }
                return [string] $version
            }

            if ($Mode -eq 'update-version') {
                try {
                    $version = Get-LatestVersion -PackageId $AppId
                } catch {
                    [Console]::Error.WriteLine("could not determine the latest version: $_")
                    exit 1
                }
                [Console]::Out.WriteLine($version)
                exit 0
            }

            # --update mode: runs on the managed Windows host itself, where choco actually exists.
            if (-not (Get-Command choco -ErrorAction SilentlyContinue)) {
                [Console]::Error.WriteLine('chocolatey is not installed')
                exit 1
            }

            # -y accepts the prompts choco would otherwise block on forever unattended;
            # --no-progress keeps a download's progress redraws out of the captured log.
            choco upgrade $AppId -y --no-progress --limit-output
            $code = $LASTEXITCODE
            # 0 = upgraded (or already current - choco reports that as success), 1641/3010 = the
            # package's own installer asked for a reboot, which is not a failure of the upgrade.
            if ($code -eq 0 -or $code -eq 1641 -or $code -eq 3010) {
                exit 0
            }
            [Console]::Error.WriteLine("choco upgrade exited with $code")
            exit 1
            """;
    }
}
