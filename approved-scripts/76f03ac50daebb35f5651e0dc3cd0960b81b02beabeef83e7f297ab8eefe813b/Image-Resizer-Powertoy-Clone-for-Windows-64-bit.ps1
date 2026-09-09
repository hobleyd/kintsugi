Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# WARNING: bricelam/ImageResizer (the app named by --appId) is archived upstream and its
# GitHub repo states "All future bug fixes, enhancements, and releases ... will be done as
# part of the Microsoft PowerToys project" (last standalone release was v3.1.2, Dec 2019).
# Per explicit instruction, this tool therefore installs the latest microsoft/PowerToys
# release and removes the legacy ImageResizer install, rather than "updating" ImageResizer
# itself. PowerToys release assets embed the version number in their filename (there is no
# fixed-name ".../releases/latest/download/<asset>" URL for it), so the download URL must be
# built from the version discovered via the GitHub redirect trick. Silent-install switches
# (/install /quiet /norestart) for the PowerToys EXE bootstrapper are confirmed via
# Microsoft's own docs (learn.microsoft.com / windows-dev-docs), and the legacy app's own
# QuietUninstallString/UninstallString registry values (written by its WiX bundle) are used
# verbatim to remove it rather than guessing its install path. These have not been verified
# by an actual run against a live Windows host in this pass.

function Write-Usage {
    [Console]::Error.WriteLine('Usage: script.ps1 --appName <name> --appId <id> (--update-version | --update)')
}

function Get-LatestPowerToysVersion {
    $releaseUrl = 'https://github.com/microsoft/PowerToys/releases/latest'
    $response = Invoke-WebRequest -Uri $releaseUrl -MaximumRedirection 0 -SkipHttpErrorCheck -ErrorAction Stop
    $location = $response.Headers['Location']
    if ($location -is [System.Array]) {
        $location = $location[0]
    }
    if (-not $location) {
        throw "No redirect Location header returned from $releaseUrl"
    }
    $tag = ($location.TrimEnd('/') -split '/')[-1]
    $version = $tag.TrimStart('v')
    if ($version -notmatch '^\d+(\.\d+){1,3}$') {
        throw "Unexpected release tag format: $tag"
    }
    return $version
}

function Get-RegistryUninstallRoot {
    return @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )
}

function Get-LegacyAppEntry {
    param([string]$AppId)
    foreach ($root in (Get-RegistryUninstallRoot)) {
        $path = Join-Path $root $AppId
        if (Test-Path -LiteralPath $path) {
            return Get-ItemProperty -LiteralPath $path
        }
    }
    return $null
}

function Find-PowerToysEntry {
    foreach ($root in (Get-RegistryUninstallRoot)) {
        if (-not (Test-Path -LiteralPath $root)) {
            continue
        }
        $children = Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue
        foreach ($child in $children) {
            $props = Get-ItemProperty -LiteralPath $child.PSPath -ErrorAction SilentlyContinue
            if ($props -and $props.PSObject.Properties['DisplayName'] -and $props.DisplayName -like 'PowerToys*') {
                return $props
            }
        }
    }
    return $null
}

function Close-ProcessGracefully {
    param([string]$ProcessName)
    $procs = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
    if (-not $procs) {
        return
    }
    foreach ($proc in $procs) {
        [void]$proc.CloseMainWindow()
    }
    $waited = 0
    while ($waited -lt 15) {
        Start-Sleep -Seconds 1
        $waited += 1
        $procs = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
        if (-not $procs) {
            return
        }
    }
    $procs = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
    foreach ($proc in $procs) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
}

function Uninstall-LegacyApp {
    param($Entry)
    if (-not $Entry) {
        return
    }
    $cmd = $null
    if ($Entry.PSObject.Properties['QuietUninstallString'] -and $Entry.QuietUninstallString) {
        $cmd = $Entry.QuietUninstallString
    } elseif ($Entry.PSObject.Properties['UninstallString'] -and $Entry.UninstallString) {
        $cmd = $Entry.UninstallString + ' /quiet /norestart'
    } else {
        [Console]::Error.WriteLine('WARNING: legacy application has no uninstall string; skipping its removal.')
        return
    }
    $proc = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', $cmd) -WindowStyle Hidden -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        [Console]::Error.WriteLine("WARNING: legacy application uninstall exited with code $($proc.ExitCode).")
    }
}

function Install-PowerToys {
    [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'PowerToys is the fixed proper name of the product being installed, not a pluralized noun.')]
    [CmdletBinding()]
    param([string]$Version)
    $assetName = "PowerToysSetup-$Version-x64.exe"
    $downloadUrl = "https://github.com/microsoft/PowerToys/releases/download/v$Version/$assetName"
    $workDir = Join-Path $env:TEMP ([System.Guid]::NewGuid().ToString())
    New-Item -ItemType Directory -Path $workDir -Force | Out-Null
    try {
        $installerPath = Join-Path $workDir $assetName
        Invoke-WebRequest -Uri $downloadUrl -OutFile $installerPath -UseBasicParsing
        $proc = Start-Process -FilePath $installerPath -ArgumentList @('/install', '/quiet', '/norestart') -Wait -PassThru
        if ($proc.ExitCode -notin @(0, 1641, 3010)) {
            throw "PowerToys installer exited with code $($proc.ExitCode)."
        }
    } finally {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$appName = $null
$appId = $null
$updateVersionMode = $false
$updateMode = $false

$i = 0
while ($i -lt $args.Count) {
    $arg = $args[$i]
    switch ($arg) {
        '--appName' {
            if ($i + 1 -ge $args.Count) {
                Write-Usage
                exit 1
            }
            $appName = $args[$i + 1]
            $i += 2
        }
        '--appId' {
            if ($i + 1 -ge $args.Count) {
                Write-Usage
                exit 1
            }
            $appId = $args[$i + 1]
            $i += 2
        }
        '--update-version' {
            $updateVersionMode = $true
            $i += 1
        }
        '--update' {
            $updateMode = $true
            $i += 1
        }
        default {
            Write-Usage
            exit 1
        }
    }
}

if (-not $appName -or -not $appId) {
    Write-Usage
    exit 1
}
if ($updateVersionMode -eq $updateMode) {
    Write-Usage
    exit 1
}

if ($updateVersionMode) {
    try {
        $version = Get-LatestPowerToysVersion
        [Console]::Out.WriteLine($version)
        exit 0
    } catch {
        [Console]::Error.WriteLine("Failed to determine latest version: $_")
        exit 1
    }
}

try {
    $legacyEntry = Get-LegacyAppEntry -AppId $appId
    if (-not $legacyEntry) {
        [Console]::Error.WriteLine("Application key '$appId' was not found under the Uninstall registry hives; wrong app or not installed.")
        exit 1
    }

    $latestVersion = Get-LatestPowerToysVersion

    $powerToysEntry = Find-PowerToysEntry
    if ($powerToysEntry -and $powerToysEntry.PSObject.Properties['DisplayVersion'] -and ([version]$powerToysEntry.DisplayVersion) -ge ([version]$latestVersion)) {
        Uninstall-LegacyApp -Entry $legacyEntry
        [Console]::Out.WriteLine("PowerToys is already up to date at version $($powerToysEntry.DisplayVersion).")
        exit 0
    }

    Close-ProcessGracefully -ProcessName 'ImageResizer'
    Close-ProcessGracefully -ProcessName 'PowerToys'

    Install-PowerToys -Version $latestVersion

    Uninstall-LegacyApp -Entry $legacyEntry

    $finalEntry = Find-PowerToysEntry
    if (-not $finalEntry -or -not $finalEntry.PSObject.Properties['DisplayVersion'] -or ([version]$finalEntry.DisplayVersion) -lt ([version]$latestVersion)) {
        $foundVersion = if ($finalEntry) { $finalEntry.DisplayVersion } else { '<not installed>' }
        [Console]::Error.WriteLine("PowerToys update verification failed: expected at least $latestVersion but found $foundVersion.")
        exit 1
    }

    [Console]::Out.WriteLine("PowerToys updated successfully to version $($finalEntry.DisplayVersion).")
    exit 0
} catch {
    [Console]::Error.WriteLine("Update failed: $_")
    exit 1
}