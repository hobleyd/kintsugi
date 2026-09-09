#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# WARNING: This script relies on https://edgeupdates.microsoft.com/api/products?view=enterprise,
# Microsoft's own (but not formally documented) JSON feed backing the Edge for Business download
# page. Its shape (Product/Releases/Artifacts with Platform, Architecture, ProductVersion fields)
# is corroborated by multiple independent community tools (the Evergreen PowerShell module,
# community Get-EdgeEnterpriseMSI scripts) that have depended on it for years, but Microsoft could
# change it without notice since there is no versioned/contractual API guarantee. No official
# GitHub/GitLab release catalog exists for Microsoft Edge (it is closed-source); the candidate
# repositories supplied for this task are unrelated projects and were not used.

function Write-UsageAndExit {
    [Console]::Error.WriteLine('Usage: script.ps1 --appName <name> --appId <id> (--update-version | --update)')
    exit 1
}

function Get-LatestEdgeInfo {
    # Architecture is hardcoded to the Windows target being managed (x64 is the standard
    # enterprise deployment target), not detected from the host running this script -
    # --update-version must run correctly on a non-Windows host (e.g. a Linux CI/reporting
    # server), where the host's own bitness/OS has nothing to do with the managed app.
    $arch = 'x64'

    $products = Invoke-RestMethod -Uri 'https://edgeupdates.microsoft.com/api/products?view=enterprise' -UseBasicParsing
    $stable = $products | Where-Object { $_.Product -eq 'Stable' } | Select-Object -First 1
    if (-not $stable) {
        throw "Could not find the 'Stable' product in the Microsoft Edge update feed."
    }

    $release = $stable.Releases |
        Where-Object { $_.Platform -eq 'Windows' -and $_.Architecture -eq $arch } |
        Select-Object -First 1
    if (-not $release) {
        throw "Could not find a Windows/$arch release in the Microsoft Edge update feed."
    }
    if (-not $release.ProductVersion) {
        throw 'Microsoft Edge update feed release entry had no ProductVersion.'
    }

    $msiArtifact = $release.Artifacts | Where-Object { $_.ArtifactName -eq 'msi' } | Select-Object -First 1
    if (-not $msiArtifact -or -not $msiArtifact.Location) {
        throw 'Could not find an MSI artifact for the latest Microsoft Edge Stable release.'
    }

    [PSCustomObject]@{
        Version     = $release.ProductVersion
        DownloadUrl = $msiArtifact.Location
    }
}

function Get-InstalledEdgeVersion {
    param(
        [Parameter(Mandatory)]
        [string]$AppId
    )

    $candidatePaths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$AppId",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
    )

    foreach ($regPath in $candidatePaths) {
        if (Test-Path -LiteralPath $regPath) {
            $props = Get-ItemProperty -LiteralPath $regPath
            if ($props.PSObject.Properties.Name -contains 'DisplayVersion' -and $props.DisplayVersion) {
                return $props.DisplayVersion
            }
        }
    }

    throw "Uninstall registry key for '$AppId' was not found (checked 64-bit and WOW6432Node hives). Is it installed under this appId?"
}

function Stop-EdgeProcess {
    [CmdletBinding(SupportsShouldProcess)]
    param()

    $processes = Get-Process -Name 'msedge' -ErrorAction SilentlyContinue
    if (-not $processes) {
        return
    }

    if (-not $PSCmdlet.ShouldProcess('msedge processes', 'Stop')) {
        return
    }

    foreach ($proc in $processes) {
        [void]$proc.CloseMainWindow()
    }

    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Seconds 1
        $processes = Get-Process -Name 'msedge' -ErrorAction SilentlyContinue
    } while ($processes -and (Get-Date) -lt $deadline)

    if ($processes) {
        $processes | Stop-Process -Force -ErrorAction SilentlyContinue
    }
}

# ---- Argument parsing ----

$appName = $null
$appId = $null
$modeVersion = $false
$modeUpdate = $false

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
        '--update-version' { $modeVersion = $true }
        '--update' { $modeUpdate = $true }
        default { Write-UsageAndExit }
    }
    $i++
}

if ([string]::IsNullOrEmpty($appName) -or [string]::IsNullOrEmpty($appId) -or ($modeVersion -eq $modeUpdate)) {
    Write-UsageAndExit
}

# ---- Mode dispatch ----

if ($modeVersion) {
    try {
        $latest = Get-LatestEdgeInfo
        [Console]::Out.WriteLine($latest.Version)
        exit 0
    } catch {
        [Console]::Error.WriteLine("Failed to determine latest version for '$appName': $($_.Exception.Message)")
        exit 1
    }
}

try {
    $latest = Get-LatestEdgeInfo
    $installedVersion = Get-InstalledEdgeVersion -AppId $appId

    if ([version]$installedVersion -ge [version]$latest.Version) {
        [Console]::Out.WriteLine("$appName is already up to date (installed $installedVersion, latest $($latest.Version)).")
        exit 0
    }

    Stop-EdgeProcess

    $tempDir = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ([Guid]::NewGuid().ToString())
    New-Item -ItemType Directory -Path $tempDir | Out-Null
    try {
        $msiPath = Join-Path -Path $tempDir -ChildPath 'MicrosoftEdgeEnterprise.msi'
        Invoke-WebRequest -Uri $latest.DownloadUrl -OutFile $msiPath -UseBasicParsing

        $msiArgs = @('/i', $msiPath, '/qn', '/norestart')
        $installProc = Start-Process -FilePath 'msiexec.exe' -ArgumentList $msiArgs -Wait -PassThru
        if ($installProc.ExitCode -notin 0, 1641, 3010) {
            throw "msiexec.exe exited with code $($installProc.ExitCode)."
        }
    } finally {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    $finalVersion = Get-InstalledEdgeVersion -AppId $appId
    if ([version]$finalVersion -lt [version]$latest.Version) {
        [Console]::Error.WriteLine("Update verification failed for '$appName': installed version is $finalVersion, expected at least $($latest.Version).")
        exit 1
    }

    [Console]::Out.WriteLine("$appName updated successfully to version $finalVersion.")
    exit 0
} catch {
    [Console]::Error.WriteLine("Update failed for '$appName': $($_.Exception.Message)")
    exit 1
}