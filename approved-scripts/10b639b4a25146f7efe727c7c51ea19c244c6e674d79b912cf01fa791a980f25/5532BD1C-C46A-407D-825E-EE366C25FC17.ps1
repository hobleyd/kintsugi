# WARNING: DisplayLink Graphics is closed-source vendor software (Synaptics/DisplayLink).
# None of the GitHub/GitLab repositories surfaced during research relate to this
# application (they were unrelated projects matched only by generic keyword overlap), so
# there is no releases API or redirect-based "latest" endpoint to rely on as would exist
# for a GitHub-hosted project. The only public distribution channel is the vendor's own
# downloads page (https://www.synaptics.com/products/displaylink-graphics/downloads/windows),
# which has no JSON/API surface either. This script therefore scrapes that HTML page (and
# the linked, plain-text release notes file it points to) for the current release, using
# the page structure observed on 2026-09-09: a "Latest Official Drivers" heading followed
# by the current release's Download/Release Notes links, then a "Legacy Drivers" heading
# for older releases. If Synaptics redesigns that page or changes the release-notes
# wording away from "Software Package Version: X.Y.Z.W", the regexes below will need
# updating - there is no more stable mechanism available for this vendor.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$usage = 'Usage: script.ps1 --appName <name> --appId <id> (--update-version | --update)'

$parsedAppName = $null
$parsedAppId = $null
$modeUpdateVersion = $false
$modeUpdate = $false

$argIndex = 0
while ($argIndex -lt $args.Count) {
    $currentArg = $args[$argIndex]
    switch ($currentArg) {
        '--appName' {
            if (($argIndex + 1) -ge $args.Count) {
                [Console]::Error.WriteLine($usage)
                exit 1
            }
            $parsedAppName = $args[$argIndex + 1]
            $argIndex += 2
        }
        '--appId' {
            if (($argIndex + 1) -ge $args.Count) {
                [Console]::Error.WriteLine($usage)
                exit 1
            }
            $parsedAppId = $args[$argIndex + 1]
            $argIndex += 2
        }
        '--update-version' {
            $modeUpdateVersion = $true
            $argIndex += 1
        }
        '--update' {
            $modeUpdate = $true
            $argIndex += 1
        }
        default {
            [Console]::Error.WriteLine($usage)
            exit 1
        }
    }
}

if ([string]::IsNullOrEmpty($parsedAppName) -or [string]::IsNullOrEmpty($parsedAppId) -or
    ($modeUpdateVersion -and $modeUpdate) -or (-not $modeUpdateVersion -and -not $modeUpdate)) {
    [Console]::Error.WriteLine($usage)
    exit 1
}

function Resolve-AbsoluteUrl {
    param(
        [Parameter(Mandatory = $true)] [string] $BaseUrl,
        [Parameter(Mandatory = $true)] [string] $RelativeUrl
    )
    if ($RelativeUrl -match '^(?i)https?://') {
        return $RelativeUrl
    }
    $baseUri = [Uri]::new($BaseUrl)
    return ([Uri]::new($baseUri, $RelativeUrl)).AbsoluteUri
}

function Get-LatestDisplayLinkRelease {
    $downloadsPageUrl = 'https://www.synaptics.com/products/displaylink-graphics/downloads/windows'

    try {
        $mainResponse = Invoke-WebRequest -Uri $downloadsPageUrl -UseBasicParsing
    }
    catch {
        throw "Failed to fetch DisplayLink downloads page: $_"
    }
    $html = $mainResponse.Content

    $startIndex = $html.IndexOf('Latest Official Drivers', [StringComparison]::OrdinalIgnoreCase)
    if ($startIndex -lt 0) {
        throw 'Could not locate the "Latest Official Drivers" section on the DisplayLink downloads page.'
    }
    $endIndex = $html.IndexOf('Legacy Drivers', $startIndex, [StringComparison]::OrdinalIgnoreCase)
    if ($endIndex -lt 0) {
        $endIndex = $html.Length
    }
    $section = $html.Substring($startIndex, $endIndex - $startIndex)

    $ignoreCase = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    $releaseNotesMatch = [regex]::Match($section, 'href\s*=\s*["'']([^"'']*release_notes[^"'']*\.txt)["'']', $ignoreCase)
    if (-not $releaseNotesMatch.Success) {
        throw 'Could not locate the release notes link for the latest DisplayLink Windows release.'
    }
    $downloadMatch = [regex]::Match($section, 'href\s*=\s*["'']([^"'']*filetype=exe[^"'']*)["'']', $ignoreCase)
    if (-not $downloadMatch.Success) {
        throw 'Could not locate the EXE download link for the latest DisplayLink Windows release.'
    }

    $releaseNotesUrl = Resolve-AbsoluteUrl -BaseUrl $downloadsPageUrl -RelativeUrl $releaseNotesMatch.Groups[1].Value
    $downloadPageUrl = Resolve-AbsoluteUrl -BaseUrl $downloadsPageUrl -RelativeUrl $downloadMatch.Groups[1].Value

    try {
        $releaseNotesResponse = Invoke-WebRequest -Uri $releaseNotesUrl -UseBasicParsing
    }
    catch {
        throw "Failed to fetch DisplayLink release notes: $_"
    }
    $versionMatch = [regex]::Match($releaseNotesResponse.Content, 'Software Package Version:?\s*([0-9]+(?:\.[0-9]+){2,3})', $ignoreCase)
    if (-not $versionMatch.Success) {
        throw 'Could not find a "Software Package Version" number in the DisplayLink release notes.'
    }

    return [PSCustomObject]@{
        Version         = $versionMatch.Groups[1].Value
        DownloadPageUrl = $downloadPageUrl
    }
}

function Get-InstalledDisplayLinkVersion {
    param(
        [Parameter(Mandatory = $true)] [string] $AppId
    )
    $registryPaths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$AppId",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
    )
    foreach ($registryPath in $registryPaths) {
        if (Test-Path -LiteralPath $registryPath) {
            $properties = Get-ItemProperty -LiteralPath $registryPath
            if ($properties.PSObject.Properties.Name -contains 'DisplayVersion') {
                return [string]$properties.DisplayVersion
            }
        }
    }
    return $null
}

if ($modeUpdateVersion) {
    try {
        $release = Get-LatestDisplayLinkRelease
        [Console]::Out.WriteLine($release.Version)
        exit 0
    }
    catch {
        [Console]::Error.WriteLine("Failed to determine latest version for $parsedAppName ($parsedAppId): $_")
        exit 1
    }
}

# --update mode: runs on the managed Windows host itself.
try {
    $latestRelease = Get-LatestDisplayLinkRelease

    $installedVersionString = Get-InstalledDisplayLinkVersion -AppId $parsedAppId
    if (-not $installedVersionString) {
        throw "$parsedAppName is not installed: no uninstall registry key found for app ID '$parsedAppId'."
    }

    $installedVersion = [version]$installedVersionString
    $latestVersion = [version]$latestRelease.Version

    if ($installedVersion -ge $latestVersion) {
        [Console]::Out.WriteLine("$parsedAppName is already up to date (installed $installedVersionString, latest $($latestRelease.Version)).")
        exit 0
    }

    $runningProcesses = Get-Process -Name 'DisplayLink*' -ErrorAction SilentlyContinue
    if ($runningProcesses) {
        foreach ($runningProcess in $runningProcesses) {
            try {
                [void]$runningProcess.CloseMainWindow()
            }
            catch {
                [void]$_
            }
        }
        $waitedSeconds = 0
        while ($waitedSeconds -lt 15) {
            Start-Sleep -Seconds 1
            $waitedSeconds += 1
            if (-not (Get-Process -Name 'DisplayLink*' -ErrorAction SilentlyContinue)) {
                break
            }
        }
        $stillRunning = Get-Process -Name 'DisplayLink*' -ErrorAction SilentlyContinue
        if ($stillRunning) {
            $stillRunning | Stop-Process -Force -ErrorAction SilentlyContinue
        }
    }

    $workDir = Join-Path -Path $env:TEMP -ChildPath ('displaylink_update_{0}' -f ([guid]::NewGuid().ToString('N')))
    New-Item -ItemType Directory -Path $workDir -Force | Out-Null
    try {
        $ignoreCase = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
        $landingResponse = Invoke-WebRequest -Uri $latestRelease.DownloadPageUrl -UseBasicParsing
        $exeMatch = [regex]::Match($landingResponse.Content, 'href\s*=\s*["'']([^"'']*exe_files[^"'']*\.exe)["'']', $ignoreCase)
        if (-not $exeMatch.Success) {
            throw 'Could not locate the direct EXE download link on the DisplayLink EULA landing page.'
        }
        $exeUrl = Resolve-AbsoluteUrl -BaseUrl $latestRelease.DownloadPageUrl -RelativeUrl $exeMatch.Groups[1].Value

        $installerPath = Join-Path -Path $workDir -ChildPath 'DisplayLinkInstaller.exe'
        Invoke-WebRequest -Uri $exeUrl -OutFile $installerPath -UseBasicParsing

        $installProcess = Start-Process -FilePath $installerPath -ArgumentList @('-silent', '-suppressUpToDateInfo') -Wait -PassThru
        if ($installProcess.ExitCode -ne 0) {
            throw "DisplayLink installer for $parsedAppName exited with code $($installProcess.ExitCode)."
        }
    }
    finally {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    $finalVersionString = Get-InstalledDisplayLinkVersion -AppId $parsedAppId
    if ((-not $finalVersionString) -or ([version]$finalVersionString -lt $latestVersion)) {
        throw "Update did not result in the expected version (found '$finalVersionString', expected at least '$($latestRelease.Version)')."
    }

    [Console]::Out.WriteLine("$parsedAppName updated successfully to version $finalVersionString.")
    exit 0
}
catch {
    [Console]::Error.WriteLine("$parsedAppName update failed: $_")
    exit 1
}