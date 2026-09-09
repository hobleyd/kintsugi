# WARNING: LibreOffice (The Document Foundation) is not hosted on GitHub/GitLab —
# the only repo found for its app ID/name ("Tbird-ops/librefuzzer") is an unrelated
# fuzzing project. Verified live on 2026-09-09 that the vendor's own download server
# (download.documentfoundation.org) exposes a plain Apache directory listing under
# /libreoffice/stable/ with one subfolder per released version, and confirmed the
# Windows x86-64 MSI filename pattern (LibreOffice_<version>_Win_x86-64.msi) inside a
# version folder. The vendor does not publish a version-agnostic "latest" URL, so the
# download link is built from the version string scraped from that listing (which is
# the officially supported distribution point, just not a stable alias). If The
# Document Foundation ever restructures that listing's HTML, the regex below would need
# updating.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-UsageAndExit {
    [Console]::Error.WriteLine('Usage: script.ps1 --appName <name> --appId <id> (--update-version | --update)')
    exit 1
}

function Get-LibreOfficeLatestVersion {
    [CmdletBinding()]
    param()

    $uri = 'https://download.documentfoundation.org/libreoffice/stable/'
    $response = Invoke-WebRequest -Uri $uri -UseBasicParsing

    $versionMatches = [regex]::Matches($response.Content, 'href="(\d+\.\d+\.\d+)/"')
    if ($versionMatches.Count -eq 0) {
        throw "No version folders found in the stable release listing at $uri"
    }

    $latest = $versionMatches |
        ForEach-Object { [version]$_.Groups[1].Value } |
        Sort-Object -Descending |
        Select-Object -First 1

    return $latest.ToString()
}

function Get-InstalledDisplayVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ApplicationId
    )

    $candidatePaths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$ApplicationId",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$ApplicationId"
    )

    $keyPath = $candidatePaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $keyPath) {
        throw "No uninstall registry key found for application ID '$ApplicationId'. The application is not installed, or the ID is incorrect."
    }

    return (Get-ItemProperty -LiteralPath $keyPath -Name 'DisplayVersion').DisplayVersion
}

function Stop-LibreOfficeProcess {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [string[]] $ProcessName = @('soffice', 'soffice.bin')
    )

    foreach ($name in $ProcessName) {
        $running = Get-Process -Name $name -ErrorAction SilentlyContinue
        if (-not $running) {
            continue
        }
        if (-not $PSCmdlet.ShouldProcess($name, 'Close running process')) {
            continue
        }

        foreach ($proc in $running) {
            [void]$proc.CloseMainWindow()
        }

        $elapsed = 0
        while ($elapsed -lt 15 -and (Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            Start-Sleep -Seconds 1
            $elapsed++
        }

        $stillRunning = Get-Process -Name $name -ErrorAction SilentlyContinue
        if ($stillRunning) {
            $stillRunning | Stop-Process -Force
        }
    }
}

# --- Argument parsing ---

$appName = $null
$appId = $null
$modeVersionCheck = $false
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
        '--update-version' { $modeVersionCheck = $true }
        '--update' { $modeUpdate = $true }
        default { Write-UsageAndExit }
    }
    $i++
}

if ([string]::IsNullOrWhiteSpace($appName) -or [string]::IsNullOrWhiteSpace($appId)) {
    Write-UsageAndExit
}
if ($modeVersionCheck -eq $modeUpdate) {
    Write-UsageAndExit
}

# --- Mode: --update-version ---

if ($modeVersionCheck) {
    try {
        $latestVersion = Get-LibreOfficeLatestVersion
        [Console]::Out.WriteLine($latestVersion)
        exit 0
    } catch {
        [Console]::Error.WriteLine("Failed to determine latest $appName version: $($_.Exception.Message)")
        exit 1
    }
}

# --- Mode: --update ---

try {
    $latestVersion = Get-LibreOfficeLatestVersion
    $installedVersion = Get-InstalledDisplayVersion -ApplicationId $appId

    if ([version]$installedVersion -ge [version]$latestVersion) {
        [Console]::Out.WriteLine("$appName is already up to date (installed $installedVersion, latest $latestVersion).")
        exit 0
    }

    $assetName = "LibreOffice_${latestVersion}_Win_x86-64.msi"
    $downloadUri = "https://download.documentfoundation.org/libreoffice/stable/$latestVersion/win/x86_64/$assetName"

    $workDir = Join-Path -Path $env:TEMP -ChildPath ('libreoffice-update-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $workDir | Out-Null

    $resultMessage = $null
    try {
        Stop-LibreOfficeProcess -ProcessName @('soffice', 'soffice.bin')

        $installerPath = Join-Path -Path $workDir -ChildPath $assetName
        Invoke-WebRequest -Uri $downloadUri -OutFile $installerPath -UseBasicParsing

        $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', $installerPath, '/qn', '/norestart') -Wait -PassThru
        if ($proc.ExitCode -notin 0, 1641, 3010) {
            throw "msiexec exited with code $($proc.ExitCode) while installing $assetName."
        }

        $finalVersion = Get-InstalledDisplayVersion -ApplicationId $appId
        if ([version]$finalVersion -lt [version]$latestVersion) {
            throw "Installer reported success (msiexec exit code $($proc.ExitCode)) but installed version is still $finalVersion, expected at least $latestVersion."
        }

        $resultMessage = "$appName updated to $finalVersion (target was $latestVersion)."
    } finally {
        if (Test-Path -LiteralPath $workDir) {
            Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    [Console]::Out.WriteLine($resultMessage)
    exit 0
} catch {
    [Console]::Error.WriteLine("Failed to update $appName ($appId): $($_.Exception.Message)")
    exit 1
}