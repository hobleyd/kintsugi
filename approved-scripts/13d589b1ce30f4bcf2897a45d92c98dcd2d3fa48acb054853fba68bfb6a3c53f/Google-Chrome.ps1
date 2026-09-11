#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# WARNING: Google Chrome is not hosted on GitHub/GitLab; it is Google's own closed-shipping
# product. Latest-version discovery uses Google's public, unauthenticated, no-rate-limit
# "Chrome Version History" JSON API (versionhistory.googleapis.com), which is the vendor's
# own documented source of truth for released Chrome versions. Installation uses Google's
# official "evergreen" Enterprise MSI URLs (dl.google.com/dl/chrome/install/...), which
# always resolve to whatever build is currently latest, so no version-specific URL is
# constructed. Both endpoints were live-verified while writing this script (7 Sep 2026).
#
# REPAIR NOTE (2026-09-11, after failure on HTWHPBCD6106928):
# The run reported "Post-install verification failed: installed version is 153.0.8010.37,
# expected at least 154.0.8037.17" -- and crucially it did NOT report an msiexec failure,
# which means msiexec.exe returned one of the accepted success codes (0/1641/3010) yet the
# DisplayVersion registry value had not actually changed at the instant we re-read it.
# Google's standalone enterprise MSI is a bootstrapper: its custom action invokes the
# bundled chrome_installer.exe, which hands the final file-copy/registration step off to a
# short-lived helper process, and that helper can still be finishing (and writing the new
# DisplayVersion) for a few seconds after msiexec.exe itself has already returned control.
# The original script read the registry exactly once, immediately after -Wait returned,
# so it raced that finalization and failed even though the upgrade would have completed a
# moment later. Fix: after msiexec exits successfully, poll DisplayVersion for up to 60s
# (every 3s) before declaring failure, instead of checking it exactly once. Everything else
# (argument parsing, process shutdown, download, msiexec invocation/exit-code handling) was
# already correct and is unchanged.

function Get-ChromeLatestVersion {
    $uri = 'https://versionhistory.googleapis.com/v1/chrome/platforms/win64/channels/stable/versions/all/releases?filter=endtime%3Dnone&order_by=version%20desc&pageSize=1'
    $response = Invoke-RestMethod -Uri $uri -Method Get -UseBasicParsing
    if (-not $response.releases -or $response.releases.Count -eq 0) {
        throw 'Chrome version history API returned no releases.'
    }
    $version = $response.releases[0].version
    if (-not $version -or $version -notmatch '^\d+(\.\d+){3}$') {
        throw "Chrome version history API returned an unexpected version string: '$version'"
    }
    return $version
}

function ConvertTo-VersionObject {
    param([string]$VersionString)
    return [System.Version]::Parse($VersionString)
}

$parsedArgs = @{
    AppName = $null
    AppId = $null
    UpdateVersion = $false
    Update = $false
}

$i = 0
while ($i -lt $args.Count) {
    switch ($args[$i]) {
        '--appName' {
            if ($i + 1 -ge $args.Count) { $parsedArgs.AppName = $null; break }
            $i++
            $parsedArgs.AppName = $args[$i]
        }
        '--appId' {
            if ($i + 1 -ge $args.Count) { $parsedArgs.AppId = $null; break }
            $i++
            $parsedArgs.AppId = $args[$i]
        }
        '--update-version' { $parsedArgs.UpdateVersion = $true }
        '--update' { $parsedArgs.Update = $true }
        default {
            [Console]::Error.WriteLine('Usage: script.ps1 --appName <name> --appId <id> (--update-version | --update)')
            exit 1
        }
    }
    $i++
}

$modeCount = 0
if ($parsedArgs.UpdateVersion) { $modeCount++ }
if ($parsedArgs.Update) { $modeCount++ }

if (-not $parsedArgs.AppName -or -not $parsedArgs.AppId -or $modeCount -ne 1) {
    [Console]::Error.WriteLine('Usage: script.ps1 --appName <name> --appId <id> (--update-version | --update)')
    exit 1
}

if ($parsedArgs.UpdateVersion) {
    try {
        $latest = Get-ChromeLatestVersion
        [Console]::Out.WriteLine($latest)
        exit 0
    } catch {
        [Console]::Error.WriteLine("Failed to determine latest Chrome version: $($_.Exception.Message)")
        exit 1
    }
}

if ($parsedArgs.Update) {
    try {
        $latestVersionString = Get-ChromeLatestVersion
    } catch {
        [Console]::Error.WriteLine("Failed to determine latest Chrome version: $($_.Exception.Message)")
        exit 1
    }
    $latestVersion = ConvertTo-VersionObject -VersionString $latestVersionString

    $uninstallKey64 = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$($parsedArgs.AppId)"
    $uninstallKey32 = "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$($parsedArgs.AppId)"

    $registryKeyPath = $null
    if (Test-Path -LiteralPath $uninstallKey64) {
        $registryKeyPath = $uninstallKey64
    } elseif (Test-Path -LiteralPath $uninstallKey32) {
        $registryKeyPath = $uninstallKey32
    } else {
        [Console]::Error.WriteLine("Application id '$($parsedArgs.AppId)' was not found under the Windows uninstall registry (checked 64-bit and 32-bit locations). It may not be installed.")
        exit 1
    }

    $installedVersionString = (Get-ItemProperty -LiteralPath $registryKeyPath -Name 'DisplayVersion').DisplayVersion
    if (-not $installedVersionString) {
        [Console]::Error.WriteLine("Registry key '$registryKeyPath' has no DisplayVersion value.")
        exit 1
    }
    $installedVersion = ConvertTo-VersionObject -VersionString $installedVersionString

    if ($installedVersion -ge $latestVersion) {
        [Console]::Out.WriteLine("$($parsedArgs.AppName) is already up to date (installed $installedVersionString, latest $latestVersionString).")
        exit 0
    }

    [Console]::Out.WriteLine("$($parsedArgs.AppName) is out of date (installed $installedVersionString, latest $latestVersionString). Updating...")

    $chromeProcess = Get-Process -Name 'chrome' -ErrorAction SilentlyContinue
    if ($chromeProcess) {
        [Console]::Out.WriteLine('Chrome is running; closing gracefully before update.')
        $chromeProcess | ForEach-Object { $null = $_.CloseMainWindow() }
        $waited = 0
        while ((Get-Process -Name 'chrome' -ErrorAction SilentlyContinue) -and $waited -lt 15) {
            Start-Sleep -Seconds 1
            $waited++
        }
        $stillRunning = Get-Process -Name 'chrome' -ErrorAction SilentlyContinue
        if ($stillRunning) {
            [Console]::Out.WriteLine('Chrome did not exit gracefully within the grace period; forcing termination.')
            $stillRunning | Stop-Process -Force -ErrorAction SilentlyContinue
        }
    }

    $is64BitOs = [Environment]::Is64BitOperatingSystem
    $downloadUrl = if ($is64BitOs) {
        'https://dl.google.com/dl/chrome/install/googlechromestandaloneenterprise64.msi'
    } else {
        'https://dl.google.com/dl/chrome/install/googlechromestandaloneenterprise.msi'
    }

    $workDir = Join-Path -Path $env:TEMP -ChildPath ("chrome-update-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $workDir | Out-Null

    try {
        $msiPath = Join-Path -Path $workDir -ChildPath 'googlechromestandaloneenterprise.msi'
        Invoke-WebRequest -Uri $downloadUrl -OutFile $msiPath -UseBasicParsing

        $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList '/i', "`"$msiPath`"", '/qn', '/norestart' -Wait -PassThru
        $exitCode = $process.ExitCode
        if ($exitCode -ne 0 -and $exitCode -ne 1641 -and $exitCode -ne 3010) {
            [Console]::Error.WriteLine("msiexec failed installing Chrome with exit code $exitCode.")
            exit 1
        }
    } finally {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    # The MSI's own custom action can hand off final file registration to a short-lived
    # helper process that is still finishing after msiexec.exe has already returned, so
    # poll for the registry to catch up instead of checking it exactly once (see REPAIR
    # NOTE above -- this loop is the actual fix for the failure this run hit).
    $maxWaitSeconds = 60
    $pollIntervalSeconds = 3
    $elapsedWaitSeconds = 0
    $postInstallVersionString = $null
    $postInstallVersion = $null
    do {
        if (-not (Test-Path -LiteralPath $registryKeyPath)) {
            [Console]::Error.WriteLine("Post-install verification failed: registry key '$registryKeyPath' no longer exists.")
            exit 1
        }
        try {
            $postInstallVersionString = (Get-ItemProperty -LiteralPath $registryKeyPath -Name 'DisplayVersion').DisplayVersion
        } catch {
            $postInstallVersionString = $null
        }
        if ($postInstallVersionString) {
            $postInstallVersion = ConvertTo-VersionObject -VersionString $postInstallVersionString
            if ($postInstallVersion -ge $latestVersion) {
                break
            }
        }
        Start-Sleep -Seconds $pollIntervalSeconds
        $elapsedWaitSeconds += $pollIntervalSeconds
    } while ($elapsedWaitSeconds -lt $maxWaitSeconds)

    if (-not $postInstallVersionString) {
        [Console]::Error.WriteLine("Post-install verification failed: '$registryKeyPath' has no DisplayVersion value.")
        exit 1
    }
    if ($postInstallVersion -lt $latestVersion) {
        [Console]::Error.WriteLine("Post-install verification failed: installed version is $postInstallVersionString, expected at least $latestVersionString.")
        exit 1
    }

    [Console]::Out.WriteLine("$($parsedArgs.AppName) successfully updated to $postInstallVersionString.")
    exit 0
}