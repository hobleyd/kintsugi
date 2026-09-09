Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# WARNING: Firefox ESR's silent-install switch (/S) and its bouncer "latest" redirect URL
# (download.mozilla.org?product=firefox-esr-latest-ssl) were verified live against Mozilla's
# services and community/enterprise documentation at the time this script was written, and the
# discovered version matched product-details.mozilla.org's FIREFOX_ESR field exactly.
# Firefox is documented (Mozilla Bugzilla #726781 and multiple enterprise deployment guides) to
# write its uninstall registry key using a name that embeds the version/arch/locale string
# (e.g. "Mozilla Firefox 140.15.0 ESR (x64 en-US)") and to create a NEW key with each version
# rather than updating one stable key in place. This script therefore falls back to a pattern
# match against the Uninstall tree when re-verifying post-install, since the exact --appId key
# handed in on the command line is expected to disappear the moment the update succeeds.

$BouncerUri = 'https://download.mozilla.org/?product=firefox-esr-latest-ssl&os=win64&lang=en-US'
$UsageMessage = 'Usage: script.ps1 --appName <name> --appId <id> (--update-version | --update)'

function Get-LatestFirefoxEsrVersion {
    try {
        $response = Invoke-WebRequest -Uri $BouncerUri -MaximumRedirection 0 -SkipHttpErrorCheck
    } catch {
        throw "Failed to contact Mozilla download service: $($_.Exception.Message)"
    }

    $location = $response.Headers['Location']
    if ($location -is [System.Array]) {
        $location = $location[0]
    }
    if ([string]::IsNullOrEmpty($location)) {
        throw "Mozilla download service did not return a redirect Location header (status $($response.StatusCode))."
    }

    if ($location -notmatch '/releases/(?<version>[0-9][0-9.]*)esr/') {
        throw "Could not parse a version number out of redirect location: $location"
    }

    return $Matches['version']
}

function Get-InstalledFirefoxEntry {
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
            if ($props.PSObject.Properties['DisplayVersion']) {
                return [PSCustomObject]@{ Path = $path; Version = $props.DisplayVersion }
            }
        }
    }

    return $null
}

function Find-FirefoxUninstallEntry {
    param(
        [Parameter(Mandatory = $true)]
        [string] $AppId,
        [Parameter(Mandatory = $true)]
        [string] $OldVersion
    )

    $exact = Get-InstalledFirefoxEntry -AppId $AppId
    if ($exact) {
        return $exact
    }

    $escapedAppId = [regex]::Escape($AppId)
    $escapedOldVersion = [regex]::Escape($OldVersion)
    $pattern = '^' + ($escapedAppId -replace $escapedOldVersion, '\d[\d.]*') + '$'

    $roots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )

    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) {
            continue
        }
        foreach ($key in Get-ChildItem -LiteralPath $root) {
            $leafName = Split-Path -Leaf $key.PSPath
            if ($leafName -match $pattern) {
                $props = Get-ItemProperty -LiteralPath $key.PSPath
                if ($props.PSObject.Properties['DisplayVersion']) {
                    return [PSCustomObject]@{ Path = $key.PSPath; Version = $props.DisplayVersion }
                }
            }
        }
    }

    return $null
}

function Stop-FirefoxProcess {
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
    param()

    $processName = 'firefox'
    $running = Get-Process -Name $processName -ErrorAction SilentlyContinue
    if (-not $running) {
        return
    }

    foreach ($proc in $running) {
        if ($PSCmdlet.ShouldProcess("$processName (PID $($proc.Id))", 'Close main window')) {
            [void]$proc.CloseMainWindow()
        }
    }

    $elapsedSeconds = 0
    while ((Get-Process -Name $processName -ErrorAction SilentlyContinue) -and $elapsedSeconds -lt 15) {
        Start-Sleep -Seconds 1
        $elapsedSeconds++
    }

    $stillRunning = Get-Process -Name $processName -ErrorAction SilentlyContinue
    if ($stillRunning) {
        foreach ($proc in $stillRunning) {
            if ($PSCmdlet.ShouldProcess("$processName (PID $($proc.Id))", 'Stop-Process -Force')) {
                $proc | Stop-Process -Force
            }
        }
    }
}

# ---- argument parsing ----

$appName = $null
$appId = $null
$modeVersion = $false
$modeUpdate = $false

$i = 0
while ($i -lt $args.Count) {
    $arg = $args[$i]
    if ($arg -eq '--appName') {
        if ($i + 1 -ge $args.Count) {
            [Console]::Error.WriteLine($UsageMessage)
            exit 1
        }
        $appName = $args[$i + 1]
        $i += 2
    } elseif ($arg -eq '--appId') {
        if ($i + 1 -ge $args.Count) {
            [Console]::Error.WriteLine($UsageMessage)
            exit 1
        }
        $appId = $args[$i + 1]
        $i += 2
    } elseif ($arg -eq '--update-version') {
        $modeVersion = $true
        $i += 1
    } elseif ($arg -eq '--update') {
        $modeUpdate = $true
        $i += 1
    } else {
        [Console]::Error.WriteLine($UsageMessage)
        exit 1
    }
}

if ([string]::IsNullOrEmpty($appName) -or [string]::IsNullOrEmpty($appId)) {
    [Console]::Error.WriteLine($UsageMessage)
    exit 1
}

if ($modeVersion -eq $modeUpdate) {
    [Console]::Error.WriteLine($UsageMessage)
    exit 1
}

# ---- mode: --update-version ----

if ($modeVersion) {
    try {
        $latestVersion = Get-LatestFirefoxEsrVersion
    } catch {
        [Console]::Error.WriteLine("Failed to determine latest Firefox ESR version: $($_.Exception.Message)")
        exit 1
    }

    [Console]::Out.WriteLine($latestVersion)
    exit 0
}

# ---- mode: --update ----

try {
    $latestVersion = Get-LatestFirefoxEsrVersion
} catch {
    [Console]::Error.WriteLine("Failed to determine latest Firefox ESR version: $($_.Exception.Message)")
    exit 1
}

$installedEntry = Get-InstalledFirefoxEntry -AppId $appId
if (-not $installedEntry) {
    [Console]::Error.WriteLine("$appName (appId '$appId') was not found under the Windows Uninstall registry key; refusing to proceed.")
    exit 1
}

if ([version]$installedEntry.Version -ge [version]$latestVersion) {
    [Console]::Out.WriteLine("$appName is already up to date (installed $($installedEntry.Version), latest $latestVersion).")
    exit 0
}

Stop-FirefoxProcess

$workDir = Join-Path $env:TEMP ('ffesr_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workDir | Out-Null
$installerPath = Join-Path $workDir 'FirefoxESRSetup.exe'

try {
    try {
        Invoke-WebRequest -Uri $BouncerUri -OutFile $installerPath
    } catch {
        throw "Failed to download Firefox ESR installer: $($_.Exception.Message)"
    }

    $installProc = Start-Process -FilePath $installerPath -ArgumentList '/S' -Wait -PassThru
    if ($installProc.ExitCode -ne 0) {
        throw "Firefox ESR installer exited with code $($installProc.ExitCode)."
    }

    $finalEntry = Find-FirefoxUninstallEntry -AppId $appId -OldVersion $installedEntry.Version
    if (-not $finalEntry) {
        throw "Could not locate the Firefox ESR uninstall registry entry after installation."
    }

    if ([version]$finalEntry.Version -lt [version]$latestVersion) {
        throw "Post-install version $($finalEntry.Version) is still below latest $latestVersion."
    }

    [Console]::Out.WriteLine("Updated $appName from $($installedEntry.Version) to $($finalEntry.Version).")
} finally {
    Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
}

exit 0