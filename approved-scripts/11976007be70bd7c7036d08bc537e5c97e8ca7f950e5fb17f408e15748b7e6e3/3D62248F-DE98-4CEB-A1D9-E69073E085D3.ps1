# WARNING: Google Earth Pro is closed-source freeware with no public release API, no
# GitHub/GitLab hosting (the candidate repos found for this search were unrelated), and
# no MSI published at a stable URL - Google only publishes a version-specific EXE
# bootstrapper. Latest-version discovery therefore scrapes Google's own "Update Google
# Earth Pro" support article (support.google.com/earth/answer/168344), which is the
# vendor's own site but is HTML, not an API, and is thus more fragile than a GitHub
# releases redirect. That page only publishes 3-component versions (e.g. 7.3.7) while
# the installed DisplayVersion has a 4th build component (e.g. 7.3.7.1327), so version
# comparisons are truncated to the first 3 components - a new build shipped under an
# unchanged 7.3.x label would not be detected as an update. The EXE installer's
# "OMAHA=1" silent-install switch and its exit-code semantics are only documented by
# third-party deployment guides (Google Earth Community threads, silentinstallhq.com),
# not an official Google reference, so this script treats exit code 0 as success but
# leans on the mandatory post-install registry re-check as the authoritative signal.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-UsageAndExit {
    [Console]::Error.WriteLine('Usage: script.ps1 --appName <name> --appId <id> (--update-version | --update)')
    exit 1
}

function Get-LatestReleaseVersion {
    $url = 'https://support.google.com/earth/answer/168344?hl=en'
    $response = Invoke-WebRequest -Uri $url -UseBasicParsing
    $pattern = 'googleearthprowin-(\d+\.\d+\.\d+)(?:-x64)?\.exe'
    $exeMatches = [regex]::Matches($response.Content, $pattern, 'IgnoreCase')
    if ($exeMatches.Count -eq 0) {
        throw 'Could not find any Google Earth Pro Windows download links on the release notes page.'
    }
    $versions = $exeMatches | ForEach-Object { [version]$_.Groups[1].Value } | Sort-Object
    return $versions[-1].ToString()
}

function Get-InstalledDisplayVersion {
    param(
        [Parameter(Mandatory = $true)][string]$AppId,
        [switch]$AllowMissing
    )
    $regPaths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$AppId",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
    )
    foreach ($regPath in $regPaths) {
        if (Test-Path -LiteralPath $regPath) {
            $props = Get-ItemProperty -LiteralPath $regPath
            if ($props.PSObject.Properties.Name -contains 'DisplayVersion') {
                return $props.DisplayVersion
            }
        }
    }
    if ($AllowMissing) {
        return $null
    }
    throw "No uninstall registry entry with a DisplayVersion was found for app ID '$AppId'."
}

function ConvertTo-TruncatedVersion {
    param(
        [Parameter(Mandatory = $true)][string]$VersionString,
        [Parameter(Mandatory = $true)][int]$PartCount
    )
    $parts = $VersionString.Split('.')
    if ($parts.Count -gt $PartCount) {
        $parts = $parts[0..($PartCount - 1)]
    }
    return [version]($parts -join '.')
}

$appName = $null
$appId = $null
$versionMode = $false
$updateMode = $false

$i = 0
while ($i -lt $args.Count) {
    switch ($args[$i]) {
        '--appName' {
            $i++
            if ($i -ge $args.Count) { Write-UsageAndExit }
            $appName = $args[$i]
        }
        '--appId' {
            $i++
            if ($i -ge $args.Count) { Write-UsageAndExit }
            $appId = $args[$i]
        }
        '--update-version' { $versionMode = $true }
        '--update' { $updateMode = $true }
        default { Write-UsageAndExit }
    }
    $i++
}

if ([string]::IsNullOrEmpty($appName) -or [string]::IsNullOrEmpty($appId)) { Write-UsageAndExit }
if ($versionMode -eq $updateMode) { Write-UsageAndExit }

if ($versionMode) {
    try {
        $version = Get-LatestReleaseVersion
    } catch {
        [Console]::Error.WriteLine("Failed to determine latest version: $($_.Exception.Message)")
        exit 1
    }
    [Console]::Out.WriteLine($version)
    exit 0
}

# --update mode (runs on the managed Windows host)

try {
    $latestVersion = Get-LatestReleaseVersion
} catch {
    [Console]::Error.WriteLine("Failed to determine latest version: $($_.Exception.Message)")
    exit 1
}
$latestParsed = [version]$latestVersion

try {
    $installedVersion = Get-InstalledDisplayVersion -AppId $appId
} catch {
    [Console]::Error.WriteLine("Failed to determine installed version for '$appName' ($appId): $($_.Exception.Message)")
    exit 1
}

if ((ConvertTo-TruncatedVersion -VersionString $installedVersion -PartCount 3) -ge $latestParsed) {
    [Console]::Out.WriteLine("$appName is already up to date (installed version $installedVersion, latest $latestVersion).")
    exit 0
}

$runningProcesses = Get-Process -Name 'googleearth' -ErrorAction SilentlyContinue
if ($runningProcesses) {
    foreach ($proc in $runningProcesses) {
        $null = $proc.CloseMainWindow()
    }
    $elapsedSeconds = 0
    while ($elapsedSeconds -lt 15) {
        Start-Sleep -Seconds 1
        $elapsedSeconds++
        $runningProcesses = Get-Process -Name 'googleearth' -ErrorAction SilentlyContinue
        if (-not $runningProcesses) { break }
    }
    if ($runningProcesses) {
        foreach ($proc in $runningProcesses) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

$tempDir = Join-Path -Path $env:TEMP -ChildPath ('GEProUpdate_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    $arch = 'x86'
    if ($env:PROCESSOR_ARCHITECTURE -eq 'AMD64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'AMD64') {
        $arch = 'x64'
    }
    $installerFileName = if ($arch -eq 'x64') { "googleearthprowin-$latestVersion-x64.exe" } else { "googleearthprowin-$latestVersion.exe" }
    $downloadUrl = "https://dl.google.com/dl/earth/client/advanced/current/$installerFileName"
    $installerPath = Join-Path -Path $tempDir -ChildPath $installerFileName

    Invoke-WebRequest -Uri $downloadUrl -OutFile $installerPath -UseBasicParsing

    $installProcess = Start-Process -FilePath $installerPath -ArgumentList 'OMAHA=1' -Wait -PassThru
    if ($installProcess.ExitCode -ne 0) {
        throw "Google Earth Pro installer exited with code $($installProcess.ExitCode)."
    }
} finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

$verifiedVersion = $null
for ($attempt = 0; $attempt -lt 15; $attempt++) {
    $currentVersion = Get-InstalledDisplayVersion -AppId $appId -AllowMissing
    if ($currentVersion -and (ConvertTo-TruncatedVersion -VersionString $currentVersion -PartCount 3) -ge $latestParsed) {
        $verifiedVersion = $currentVersion
        break
    }
    Start-Sleep -Seconds 2
}

if (-not $verifiedVersion) {
    [Console]::Error.WriteLine("Update verification failed: '$appName' is not reporting a DisplayVersion at or above $latestVersion.")
    exit 1
}

[Console]::Out.WriteLine("$appName updated successfully to version $verifiedVersion.")
exit 0