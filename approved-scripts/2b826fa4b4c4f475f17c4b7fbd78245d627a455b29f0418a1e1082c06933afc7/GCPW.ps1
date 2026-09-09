Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# WARNING: GCPW is closed-source and not hosted on GitHub/GitLab (the candidate repos
# found for "GCPW" are unrelated projects). Google publishes no machine-readable JSON/API
# feed or GitHub-style "releases/latest" redirect for it, so the latest-version check
# below scrapes Google's own official "What's new in GCPW" help-center article
# (support.google.com/a/answer/9818093, which permanently redirects to
# knowledge.workspace.google.com/admin/devices/whats-new-in-gcpw) for the first/topmost
# "Release X.X.X.X" heading, which the article lists newest-first. This was verified live
# during research (it correctly returned 150.0.7871.100, matching the version noted as
# currently installed across managed hosts) but it depends on Google keeping that page's
# wording/ordering stable and statically renderable without JavaScript; if Google
# reformats that page this check will start failing loudly (non-zero exit), not silently.
# The download itself uses Google's stable, version-agnostic MSI URL
# (dl.google.com/credentialprovider/gcpwstandaloneenterprise64.msi), which is the same
# always-current link Google's own admin documentation uses.

function Write-UsageError {
    [Console]::Error.WriteLine('Usage: script.ps1 --appName <name> --appId <id> (--update-version | --update)')
}

function Get-LatestReleaseVersion {
    $uri = 'https://support.google.com/a/answer/9818093?hl=en'
    $response = Invoke-WebRequest -Uri $uri -UseBasicParsing
    $plainText = $response.Content -replace '<[^>]+>', ' '
    $match = [regex]::Match($plainText, 'Release\s+(\d+\.\d+\.\d+\.\d+)')
    if (-not $match.Success) {
        throw 'Could not locate a "Release <version>" entry on the GCPW release notes page.'
    }
    return $match.Groups[1].Value
}

function Get-InstalledDisplayVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string] $AppId
    )
    $candidatePaths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$AppId",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
    )
    foreach ($path in $candidatePaths) {
        if (Test-Path -LiteralPath $path) {
            $props = Get-ItemProperty -LiteralPath $path
            if ($props.PSObject.Properties.Name -contains 'DisplayVersion') {
                return [string]$props.DisplayVersion
            }
        }
    }
    return $null
}

$appName = $null
$appId = $null
$updateVersionMode = $false
$updateMode = $false

$i = 0
while ($i -lt $args.Count) {
    switch ($args[$i]) {
        '--appName' {
            $i++
            if ($i -ge $args.Count) { Write-UsageError; exit 1 }
            $appName = $args[$i]
        }
        '--appId' {
            $i++
            if ($i -ge $args.Count) { Write-UsageError; exit 1 }
            $appId = $args[$i]
        }
        '--update-version' { $updateVersionMode = $true }
        '--update' { $updateMode = $true }
        default { Write-UsageError; exit 1 }
    }
    $i++
}

if ([string]::IsNullOrEmpty($appName) -or [string]::IsNullOrEmpty($appId)) {
    Write-UsageError
    exit 1
}
if ($updateVersionMode -eq $updateMode) {
    # requires exactly one of the two mode switches
    Write-UsageError
    exit 1
}

if ($updateVersionMode) {
    try {
        $latestVersion = Get-LatestReleaseVersion
    } catch {
        [Console]::Error.WriteLine("Failed to determine latest version for $appName : $($_.Exception.Message)")
        exit 1
    }
    [Console]::Out.WriteLine($latestVersion)
    exit 0
}

# --update mode from here down: runs on the managed Windows host.

try {
    $latestVersion = Get-LatestReleaseVersion
} catch {
    [Console]::Error.WriteLine("Failed to determine latest version for $appName : $($_.Exception.Message)")
    exit 1
}

$installedVersion = Get-InstalledDisplayVersion -AppId $appId
if ($null -eq $installedVersion) {
    [Console]::Error.WriteLine("$appName (appId '$appId') is not installed: no uninstall registry key found under HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall or its WOW6432Node equivalent.")
    exit 1
}

if ([version]$installedVersion -ge [version]$latestVersion) {
    Write-Output "$appName is already up to date (installed $installedVersion, latest $latestVersion). Nothing to do."
    exit 0
}

Write-Output "$appName : updating from $installedVersion to $latestVersion."

$processName = 'gcpw_extension'
$runningProcs = Get-Process -Name $processName -ErrorAction SilentlyContinue
if ($runningProcs) {
    foreach ($proc in $runningProcs) {
        [void]$proc.CloseMainWindow()
    }
    $waitedSeconds = 0
    while ($waitedSeconds -lt 15) {
        Start-Sleep -Seconds 1
        $waitedSeconds++
        $runningProcs = Get-Process -Name $processName -ErrorAction SilentlyContinue
        if (-not $runningProcs) { break }
    }
    if ($runningProcs) {
        foreach ($proc in $runningProcs) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

$tempDir = Join-Path -Path $env:TEMP -ChildPath ('gcpw-update-' + [guid]::NewGuid().ToString())
New-Item -ItemType Directory -Path $tempDir | Out-Null
try {
    $fileName = if ([Environment]::Is64BitOperatingSystem) { 'gcpwstandaloneenterprise64.msi' } else { 'gcpwstandaloneenterprise.msi' }
    $downloadUri = "https://dl.google.com/credentialprovider/$fileName"
    $msiPath = Join-Path -Path $tempDir -ChildPath $fileName

    Invoke-WebRequest -Uri $downloadUri -OutFile $msiPath -UseBasicParsing

    $msiArgs = @('/i', $msiPath, '/qn', '/norestart')
    $installProcess = Start-Process -FilePath 'msiexec.exe' -ArgumentList $msiArgs -Wait -PassThru
    if ($installProcess.ExitCode -notin @(0, 1641, 3010)) {
        [Console]::Error.WriteLine("msiexec failed with exit code $($installProcess.ExitCode).")
        exit 1
    }
    if ($installProcess.ExitCode -ne 0) {
        Write-Output "Install succeeded; a reboot is pending (msiexec exit code $($installProcess.ExitCode))."
    }
} finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

$finalVersion = Get-InstalledDisplayVersion -AppId $appId
if ($null -eq $finalVersion -or [version]$finalVersion -lt [version]$latestVersion) {
    [Console]::Error.WriteLine("Update verification failed for $appName : installed version is now '$finalVersion', expected at least '$latestVersion'.")
    exit 1
}

Write-Output "$appName updated successfully to version $finalVersion."
exit 0