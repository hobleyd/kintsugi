namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// The Windows counterpart to <see cref="HomebrewUpgradeScript"/>: the fixed
/// --appName/--appId/--update-version/--update script for everything winget manages, winget itself
/// included. Never AI-generated — winget's upgrade mechanics are already fully known.
/// </summary>
/// <remarks>
/// <para>
/// Same two-audience split as the Homebrew script, just in PowerShell: <c>--update-version</c> runs
/// on the (Linux) API server under <c>pwsh</c> and may only make HTTP calls, while <c>--update</c>
/// runs on the managed Windows host where <c>winget.exe</c> actually exists. The package id — not
/// the display name — is what winget addresses a package by, so <c>--appId</c> is the load-bearing
/// argument here (the Windows agent reports a winget package's id as its
/// <c>applicationIdentifier</c>); neither it nor the name is ever baked into the text, so
/// <see cref="Build"/> returns byte-identical content for every winget package and one human
/// "Sign Script" review covers them all.
/// </para>
/// <para>
/// One text for winget's own row and every package it manages (see
/// <see cref="RecognizedPackageManager.BuildScript"/>): winget's own row is told
/// apart at runtime by <c>--appName</c> — the name the Windows agent reports it under
/// (<c>system_info::WINGET_NAME</c>) — because the Applications screen shows a manager's script once,
/// on the manager's row, on behalf of every package nested under it, and that is only honest if the
/// manager's bytes are its children's bytes. See the remarks on <see cref="HomebrewUpgradeScript"/>,
/// where the reasoning lives in full. winget is not a winget package under its own name (it ships
/// inside App Installer, <c>Microsoft.AppInstaller</c>), so that row needs a different version source
/// and a different id to upgrade — hence the branch rather than Chocolatey's and Snap's plain reuse.
/// </para>
/// </remarks>
public static class WingetUpgradeScript
{
    public static string Build()
    {
        // The winget-pkgs repository's manifest tree is the canonical list of published versions,
        // and a directory listing of it needs no auth. It *is* rate-limited to 60 requests/hour for
        // an unauthenticated caller, which a large fleet scan can reach — but this is the secondary
        // source for a winget row's version, not the primary one: the agent already reports each
        // package's available version straight from `winget` itself on every check-in, which
        // RegisterApplicationsCommandHandler seeds the row from. A throttled check here just leaves
        // that seeded value in place.
        //
        // Microsoft's own storeedgefd REST source was tried first and dropped: it answers
        // /packageManifests/<id> with a response whose Versions array is empty, so it looked like a
        // successful lookup while actually yielding nothing usable.
        const string latestVersionLogic = """
            # winget's own row. The agent reports winget itself under this name (system_info::WINGET_NAME),
            # and it is not a package in winget-pkgs under it: it ships inside App Installer. One script
            # serves it and everything it manages so that one review covers all of them; this is where
            # the two part ways.
            function Test-IsWingetItself {
                return ($AppName -ieq 'winget')
            }

            function Get-LatestVersion {
                param([string] $AppId)
                if (Test-IsWingetItself) {
                    # App Installer's releases are published on GitHub. The releases/latest redirect
                    # names the newest tag with no API call, and so no rate limit, at all.
                    $location = Get-RedirectLocation -Uri 'https://github.com/microsoft/winget-cli/releases/latest'
                    $tag = ($location -split '/')[-1]
                    if (-not $tag) { throw "could not read a release tag while checking $AppId" }
                    return $tag.TrimStart('v')
                }

                # winget-pkgs' fixed layout: manifests/<lowercased first letter of the id>/<the id
                # split on '.'>/, where each subdirectory name is a published version.
                $segments = $AppId -split '\.'
                $path = 'manifests/' + $AppId.Substring(0, 1).ToLowerInvariant() + '/' + ($segments -join '/')
                $listing = Invoke-RestMethod -Method Get -ErrorAction Stop `
                    -Headers @{ 'User-Agent' = 'kintsugi-agent'; 'Accept' = 'application/vnd.github+json' } `
                    -Uri "https://api.github.com/repos/microsoft/winget-pkgs/contents/$path"

                # Directories only, and only ones that look like a version — the tree also carries
                # non-version entries (a '.validation' directory, the odd stray file).
                $versions = @($listing | Where-Object { $_.type -eq 'dir' -and $_.name -match '^\d' } | ForEach-Object { $_.name })
                if ($versions.Count -eq 0) { throw "no published versions found for $AppId" }
                return ($versions | Sort-Object { ConvertTo-SortableVersion $_ } | Select-Object -Last 1)
            }
            """;

        // --exact so a partial id match can never upgrade a different package, and every
        // interactivity/agreement flag winget needs to run unattended — without them it blocks
        // forever waiting on a prompt no one will ever see. winget's own row upgrades App Installer,
        // which is the package winget actually ships in.
        const string updateCommand = """
            $upgradeId = if (Test-IsWingetItself) { 'Microsoft.AppInstaller' } else { $AppId }
            winget upgrade --exact --id $upgradeId --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
            """;

        return $$"""
            #Requires -Version 5.1
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'

            {{PowerShellUpgradeScript.VersionSortHelper}}

            {{PowerShellUpgradeScript.RedirectLocationHelper}}

            {{PowerShellUpgradeScript.ArgumentParsing}}

            {{latestVersionLogic}}

            if ($Mode -eq 'update-version') {
                try {
                    $version = Get-LatestVersion -AppId $AppId
                } catch {
                    [Console]::Error.WriteLine("could not determine the latest version: $_")
                    exit 1
                }
                if (-not $version) {
                    [Console]::Error.WriteLine('could not determine the latest version')
                    exit 1
                }
                [Console]::Out.WriteLine($version)
                exit 0
            }

            # --update mode: runs on the managed Windows host itself, where winget actually exists.
            if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
                [Console]::Error.WriteLine('winget is not installed (it ships with App Installer, from the Microsoft Store)')
                exit 1
            }

            {{updateCommand}}
            $code = $LASTEXITCODE
            # 0x8A15002B ("no applicable upgrade found") is winget's way of saying the package is
            # already current. That is the idempotent-success case this contract requires, not a
            # failure.
            if ($code -eq 0 -or $code -eq -1978335189) {
                exit 0
            }
            [Console]::Error.WriteLine("winget upgrade exited with $code")
            exit 1
            """;
    }
}
