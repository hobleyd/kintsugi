Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# WARNING: Mozilla does not publish a standalone download/version feed for the
# Maintenance Service itself. Its registry DisplayVersion is stamped from the
# Firefox app version that installed/updated it (confirmed via the
# maintenanceservice_installer.nsi source, which writes
# DisplayVersion=${AppVersion}), and it is only ever refreshed as a side effect
# of running a Firefox installer (the bundled maintenanceservice_installer.exe
# performs its own internal "only upgrade if newer" check, so re-running it is
# always safe/idempotent). The sample installed version supplied for this
# fleet ("140.15.0") matches the double-digit point-release pattern of the
# Firefox ESR channel (confirmed live: product-details.mozilla.org currently
# reports FIREFOX_ESR = "140.15.0esr"), so this script treats the fleet as
# running Firefox ESR and uses Mozilla's official ESR version feed and ESR MSI
# "latest" download as the source of truth. If any managed host actually runs
# the Release/Beta/Nightly channel instead, its Maintenance Service version
# numbering will not line up with the ESR feed used here.

function Show-Usage {
    [Console]::Error.WriteLine('Usage: script.ps1 --appName <name> --appId <id> (--update-version | --update)')
    exit 1
}

function Get-LatestReleaseVersion {
    try {
        $data = Invoke-RestMethod -Uri 'https://product-details.mozilla.org/1.0/firefox_versions.json' -Method Get
    } catch {
        throw "Failed to reach Mozilla product-details service: $($_.Exception.Message)"
    }

    if ($null -eq $data -or -not $data.PSObject.Properties.Name.Contains('FIREFOX_ESR')) {
        throw 'Mozilla product-details response did not contain a FIREFOX_ESR field.'
    }

    if ($data.FIREFOX_ESR -notmatch '^(?<ver>\d+(\.\d+){1,3})esr$') {
        throw "Unrecognized FIREFOX_ESR version format: '$($data.FIREFOX_ESR)'"
    }

    return $Matches['ver']
}

function Get-InstalledVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AppId
    )

    $candidatePaths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$AppId",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
    )

    foreach ($regPath in $candidatePaths) {
        if (Test-Path -LiteralPath $regPath) {
            $props = Get-ItemProperty -LiteralPath $regPath
            if ($props.PSObject.Properties.Name.Contains('DisplayVersion')) {
                return [string]$props.DisplayVersion
            }
        }
    }

    throw "No uninstall registry entry with a DisplayVersion was found for '$AppId' (checked native and WOW6432Node paths) - it may not be installed on this host."
}

# --- argument parsing ---

$appName = $null
$appId = $null
$modeVersionCheck = $false
$modeUpdate = $false

$i = 0
while ($i -lt $args.Count) {
    switch ($args[$i]) {
        '--appName' {
            if ($i + 1 -ge $args.Count) { Show-Usage }
            $appName = $args[$i + 1]
            $i += 2
        }
        '--appId' {
            if ($i + 1 -ge $args.Count) { Show-Usage }
            $appId = $args[$i + 1]
            $i += 2
        }
        '--update-version' {
            $modeVersionCheck = $true
            $i += 1
        }
        '--update' {
            $modeUpdate = $true
            $i += 1
        }
        default { Show-Usage }
    }
}

if ([string]::IsNullOrWhiteSpace($appName) -or [string]::IsNullOrWhiteSpace($appId)) { Show-Usage }
if ($modeVersionCheck -eq $modeUpdate) { Show-Usage }

# --- --update-version mode: Linux pwsh, network-only, no filesystem/registry ---

if ($modeVersionCheck) {
    try {
        $latest = Get-LatestReleaseVersion
        [Console]::Out.WriteLine($latest)
        exit 0
    } catch {
        [Console]::Error.WriteLine("Failed to determine latest version: $($_.Exception.Message)")
        exit 1
    }
}

# --- --update mode: runs on the managed Windows host as SYSTEM ---

try {
    $latestVersion = Get-LatestReleaseVersion
    $installedVersion = Get-InstalledVersion -AppId $appId

    if ([version]$installedVersion -ge [version]$latestVersion) {
        [Console]::Out.WriteLine("$appName is already up to date (installed $installedVersion, latest $latestVersion). No action taken.")
        exit 0
    }

    $serviceName = 'MozillaMaintenanceService'
    $processName = 'maintenanceservice'

    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -eq 'Running') {
        # The Maintenance Service is a background Windows service with no window
        # to close, so a Stop-Service request is its graceful-shutdown equivalent
        # to CloseMainWindow(); only fall back to a hard kill if it won't stop.
        Stop-Service -Name $serviceName -ErrorAction SilentlyContinue

        $waited = 0
        while ($waited -lt 15) {
            $runningProc = Get-Process -Name $processName -ErrorAction SilentlyContinue
            if ($null -eq $runningProc) { break }
            Start-Sleep -Seconds 1
            $waited += 1
        }

        $runningProc = Get-Process -Name $processName -ErrorAction SilentlyContinue
        if ($null -ne $runningProc) {
            Stop-Process -Name $processName -Force -ErrorAction SilentlyContinue
        }
    }

    $arch = if ([Environment]::Is64BitOperatingSystem) { 'win64' } else { 'win32' }
    $downloadUrl = "https://download.mozilla.org/?product=firefox-esr-msi-latest-ssl&os=$arch&lang=en-US"

    $tempDir = Join-Path -Path $env:TEMP -ChildPath ("MozillaMaintenanceServiceUpdate_" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

    try {
        $msiPath = Join-Path -Path $tempDir -ChildPath 'FirefoxESRSetup.msi'
        Invoke-WebRequest -Uri $downloadUrl -OutFile $msiPath -MaximumRedirection 5

        $msiArgs = @('/i', "`"$msiPath`"", '/qn', '/norestart')
        $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList $msiArgs -Wait -PassThru

        if ($proc.ExitCode -notin @(0, 1641, 3010)) {
            throw "msiexec exited with unexpected code $($proc.ExitCode)"
        }
    } finally {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    $finalVersion = Get-InstalledVersion -AppId $appId
    if ([version]$finalVersion -lt [version]$latestVersion) {
        [Console]::Error.WriteLine("Update reported success but installed version is still $finalVersion (expected at least $latestVersion).")
        exit 1
    }

    [Console]::Out.WriteLine("$appName updated successfully from $installedVersion to $finalVersion.")
    exit 0
} catch {
    [Console]::Error.WriteLine("Update failed: $($_.Exception.Message)")
    exit 1
}