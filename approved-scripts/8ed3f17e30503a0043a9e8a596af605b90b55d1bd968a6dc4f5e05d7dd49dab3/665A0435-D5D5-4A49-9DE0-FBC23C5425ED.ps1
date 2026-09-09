#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# WARNING: The comparison logic below (Get-ExpectedLauncherVersion) relies on an
# undocumented, internal CPython build formula that maps a public release version
# (e.g. 3.12.10) to the DisplayVersion string actually written into the Windows
# registry by the "Python Launcher" component's launcher.msi (e.g. 3.12.10150.0):
# Field3 = (micro * 1000) + 150. This was verified directly against
# PCbuild/python.props in the python/cpython repository (ReleaseLevelNumber=15 and
# ReleaseSerial=0 for a final release), and cross-checked against the installed
# version supplied for this host (3.12.10150.0) and historical examples from
# python/cpython issue #101849 (3.11.2 -> 3.11.2150.0). It was introduced as a
# deliberate fix to make launcher versions sort monotonically, but it is an
# internal implementation detail, not a documented public contract, and could
# change again in a future CPython release. If it does, the worst-case outcome is
# a redundant re-install attempt (this script's final verification step would
# still catch a genuinely failed update), not a wrong/broken install.
#
# WARNING: Python's classic full EXE installer (python-X.Y.Z-amd64.exe), which this
# script downloads and runs with LauncherOnly=1, is documented as deprecated
# starting with Python 3.14 and is planned to be discontinued entirely from Python
# 3.16 onward in favor of a new "Python Install Manager" distribution mechanism.
# Once the latest stable release moves past that transition, the download URL and
# silent-install arguments used here will need to be rewritten for whatever
# distribution channel replaces it.

function Write-UsageAndExit {
    [Console]::Error.WriteLine('Usage: script.ps1 --appName <name> --appId <id> (--update-version | --update)')
    exit 1
}

function Get-LatestPythonVersion {
    $indexUri = 'https://www.python.org/ftp/python/'
    $response = $null
    try {
        $response = Invoke-WebRequest -Uri $indexUri -UseBasicParsing
    }
    catch {
        throw "Failed to retrieve Python release index from $indexUri : $($_.Exception.Message)"
    }

    $versionMatches = [regex]::Matches($response.Content, 'href="(\d+\.\d+\.\d+)/"')
    if ($versionMatches.Count -eq 0) {
        throw "No stable version directories found at $indexUri"
    }

    $versions = $versionMatches | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
    $latest = $versions | Sort-Object -Property { [version]$_ } -Descending | Select-Object -First 1
    if ([string]::IsNullOrEmpty($latest)) {
        throw 'Unable to determine the latest stable Python version.'
    }

    return $latest
}

function Get-ExpectedLauncherVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PythonVersion
    )

    $parts = $PythonVersion.Split('.')
    if ($parts.Count -ne 3) {
        throw "Unexpected Python version format: $PythonVersion"
    }

    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $micro = [int]$parts[2]
    $field3 = ($micro * 1000) + 150

    return "$major.$minor.$field3.0"
}

function Get-InstalledLauncherInfo {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$RegistryPaths
    )

    foreach ($path in $RegistryPaths) {
        if (Test-Path -LiteralPath $path) {
            $item = Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue
            if ($null -ne $item -and $item.PSObject.Properties.Name -contains 'DisplayVersion') {
                return [PSCustomObject]@{
                    Path    = $path
                    Version = $item.DisplayVersion
                }
            }
        }
    }

    return $null
}

function Stop-LauncherProcess {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Name
    )

    foreach ($processName in $Name) {
        $procs = Get-Process -Name $processName -ErrorAction SilentlyContinue
        if (-not $procs) {
            continue
        }

        if ($PSCmdlet.ShouldProcess($processName, 'Stop process')) {
            foreach ($proc in $procs) {
                try {
                    [void]$proc.CloseMainWindow()
                }
                catch {
                    [Console]::Error.WriteLine("Warning: could not request graceful close of process '$processName' (PID $($proc.Id)): $($_.Exception.Message)")
                }
            }

            $waited = 0
            while ($waited -lt 15) {
                if (-not (Get-Process -Name $processName -ErrorAction SilentlyContinue)) {
                    break
                }
                Start-Sleep -Seconds 1
                $waited++
            }

            $stillRunning = Get-Process -Name $processName -ErrorAction SilentlyContinue
            if ($stillRunning) {
                $stillRunning | Stop-Process -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

# --- Argument parsing ---

$appName = $null
$appId = $null
$updateVersionFlag = $false
$updateFlag = $false

$argIndex = 0
while ($argIndex -lt $args.Count) {
    switch ($args[$argIndex]) {
        '--appName' {
            $argIndex++
            if ($argIndex -ge $args.Count) { Write-UsageAndExit }
            $appName = $args[$argIndex]
        }
        '--appId' {
            $argIndex++
            if ($argIndex -ge $args.Count) { Write-UsageAndExit }
            $appId = $args[$argIndex]
        }
        '--update-version' { $updateVersionFlag = $true }
        '--update' { $updateFlag = $true }
        default { Write-UsageAndExit }
    }
    $argIndex++
}

if ([string]::IsNullOrEmpty($appName) -or [string]::IsNullOrEmpty($appId)) {
    Write-UsageAndExit
}

if ($updateVersionFlag -eq $updateFlag) {
    # True when neither flag was given, or both were given (conflicting).
    Write-UsageAndExit
}

# --- Main ---

try {
    if ($updateVersionFlag) {
        $latestVersion = Get-LatestPythonVersion
        [Console]::Out.WriteLine($latestVersion)
        exit 0
    }

    # --update mode (runs on the managed Windows host).
    $latestVersion = Get-LatestPythonVersion
    $expectedLauncherVersion = Get-ExpectedLauncherVersion -PythonVersion $latestVersion

    $registryPaths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$appId",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
    )

    $installedInfo = Get-InstalledLauncherInfo -RegistryPaths $registryPaths
    if ($null -eq $installedInfo) {
        throw "No installed application found for '$appName' under appId '$appId' (checked both the 64-bit and WOW6432Node uninstall keys). Refusing to proceed."
    }

    if ([version]$installedInfo.Version -ge [version]$expectedLauncherVersion) {
        [Console]::Out.WriteLine("[$appName] Already up to date: installed version $($installedInfo.Version) corresponds to Python $latestVersion or newer. No changes made.")
        exit 0
    }

    [Console]::Out.WriteLine("[$appName] Update available: installed $($installedInfo.Version), latest is Python $latestVersion (expected launcher version $expectedLauncherVersion). Updating.")

    Stop-LauncherProcess -Name @('py', 'pyw')

    $tempDir = Join-Path -Path $env:TEMP -ChildPath ('pylauncher-update-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

    try {
        $installerName = "python-$latestVersion-amd64.exe"
        $installerPath = Join-Path -Path $tempDir -ChildPath $installerName
        $downloadUri = "https://www.python.org/ftp/python/$latestVersion/$installerName"

        Invoke-WebRequest -Uri $downloadUri -OutFile $installerPath -UseBasicParsing

        $installArgs = @(
            '/quiet',
            'InstallAllUsers=1',
            'Include_launcher=1',
            'InstallLauncherAllUsers=1',
            'LauncherOnly=1'
        )

        $process = Start-Process -FilePath $installerPath -ArgumentList $installArgs -Wait -PassThru
        $exitCode = $process.ExitCode

        if ($exitCode -ne 0 -and $exitCode -ne 1641 -and $exitCode -ne 3010) {
            throw "Installer '$installerName' exited with code $exitCode."
        }
    }
    finally {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    $postInstallInfo = Get-InstalledLauncherInfo -RegistryPaths $registryPaths
    if ($null -eq $postInstallInfo -or [version]$postInstallInfo.Version -lt [version]$expectedLauncherVersion) {
        $actualVersion = if ($postInstallInfo) { $postInstallInfo.Version } else { '<not found>' }
        throw "Update verification failed for '$appName': expected launcher version $expectedLauncherVersion or newer, but found $actualVersion."
    }

    [Console]::Out.WriteLine("[$appName] Successfully updated to launcher version $($postInstallInfo.Version) (Python $latestVersion).")
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}