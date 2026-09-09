#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# WARNING: PDF24 Creator (Geek Software GmbH) is closed-source freeware. The GitHub
# repository "PDF24/PDF24-Creator" (matching this vendor) is confirmed to be only a
# public issue tracker with no Releases/binaries, so this script instead scrapes the
# vendor's own changelog page (https://creator.pdf24.org/changelog) for the latest
# version number, and builds the download URL from a versioned filename pattern
# (https://download.pdf24.org/pdf24-creator-<version>-x64.msi) confirmed against the
# vendor's own "all versions" listing (https://creator.pdf24.org/listVersions.php) at
# research time (2026-09-09, current version 11.30.1). There is no version-less
# "latest" download URL on this vendor's site (unlike a GitHub releases/latest
# redirect), so if the vendor ever changes this URL pattern or the changelog's HTML
# structure, both --update-version and --update will start failing loudly (non-zero
# exit, stderr message) rather than silently misbehaving.

function Show-UsageError {
    param([string]$Message)
    [Console]::Error.WriteLine("$Message`nUsage: script.ps1 --appName <name> --appId <id> (--update-version | --update)")
    exit 1
}

function Get-LatestPdf24Version {
    $uri = 'https://creator.pdf24.org/changelog'
    try {
        $response = Invoke-WebRequest -Uri $uri -UseBasicParsing
    } catch {
        throw "Failed to fetch $uri : $_"
    }
    $match = [regex]::Match($response.Content, 'Version\s+(\d+\.\d+\.\d+)')
    if (-not $match.Success) {
        throw "Could not locate a version number in the content of $uri"
    }
    return $match.Groups[1].Value
}

function Get-Pdf24InstallInfo {
    param([Parameter(Mandatory)][string]$AppId)
    $candidatePaths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$AppId",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
    )
    foreach ($regPath in $candidatePaths) {
        if (Test-Path -LiteralPath $regPath) {
            $props = Get-ItemProperty -LiteralPath $regPath
            if ($props.PSObject.Properties.Name -contains 'DisplayVersion') {
                return [pscustomobject]@{ RegistryPath = $regPath; DisplayVersion = $props.DisplayVersion }
            }
        }
    }
    return $null
}

function Stop-Pdf24Process {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    param()

    $procs = Get-Process -Name 'pdf24' -ErrorAction SilentlyContinue
    if (-not $procs) {
        return
    }
    if ($PSCmdlet.ShouldProcess('pdf24 process(es)', 'Request graceful close (CloseMainWindow)')) {
        foreach ($proc in $procs) {
            [void]$proc.CloseMainWindow()
        }
    }
    $graceSeconds = 15
    for ($elapsed = 0; $elapsed -lt $graceSeconds; $elapsed++) {
        Start-Sleep -Seconds 1
        $procs = Get-Process -Name 'pdf24' -ErrorAction SilentlyContinue
        if (-not $procs) {
            return
        }
    }
    $procs = Get-Process -Name 'pdf24' -ErrorAction SilentlyContinue
    if ($procs -and $PSCmdlet.ShouldProcess('pdf24 process(es)', 'Force stop')) {
        Stop-Process -InputObject $procs -Force -ErrorAction SilentlyContinue
    }
}

# --- Argument parsing ---

$appName = $null
$appId = $null
$modeVersion = $false
$modeUpdate = $false

$i = 0
while ($i -lt $args.Count) {
    switch ($args[$i]) {
        '--appName' {
            $i++
            if ($i -ge $args.Count) { Show-UsageError "Missing value for --appName" }
            $appName = $args[$i]
        }
        '--appId' {
            $i++
            if ($i -ge $args.Count) { Show-UsageError "Missing value for --appId" }
            $appId = $args[$i]
        }
        '--update-version' { $modeVersion = $true }
        '--update' { $modeUpdate = $true }
        default { Show-UsageError "Unknown argument: $($args[$i])" }
    }
    $i++
}

if ([string]::IsNullOrEmpty($appName)) { Show-UsageError "Missing required argument: --appName" }
if ([string]::IsNullOrEmpty($appId)) { Show-UsageError "Missing required argument: --appId" }
if ($modeVersion -and $modeUpdate) { Show-UsageError "Specify only one of --update-version or --update" }
if (-not $modeVersion -and -not $modeUpdate) { Show-UsageError "Specify one of --update-version or --update" }

# --- Modes ---

if ($modeVersion) {
    try {
        $latestVersion = Get-LatestPdf24Version
    } catch {
        [Console]::Error.WriteLine("Failed to determine latest version for $appName : $_")
        exit 1
    }
    [Console]::Out.WriteLine($latestVersion)
    exit 0
}

# --update
try {
    $latestVersion = Get-LatestPdf24Version
} catch {
    [Console]::Error.WriteLine("Failed to determine latest version for $appName : $_")
    exit 1
}

$installInfo = Get-Pdf24InstallInfo -AppId $appId
if ($null -eq $installInfo) {
    [Console]::Error.WriteLine("$appName (appId '$appId') does not appear to be installed: no uninstall registry key found.")
    exit 1
}

$installedVersion = $installInfo.DisplayVersion
if ([version]$installedVersion -ge [version]$latestVersion) {
    [Console]::Out.WriteLine("$appName is already up to date (installed $installedVersion, latest $latestVersion). No action taken.")
    exit 0
}

Stop-Pdf24Process

$workDir = Join-Path -Path $env:TEMP -ChildPath ("pdf24_update_{0}" -f ([guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Path $workDir -Force | Out-Null

try {
    $downloadUri = "https://download.pdf24.org/pdf24-creator-$latestVersion-x64.msi"
    $msiPath = Join-Path -Path $workDir -ChildPath "pdf24-creator-$latestVersion-x64.msi"
    Invoke-WebRequest -Uri $downloadUri -OutFile $msiPath -UseBasicParsing

    $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', $msiPath, '/qn', '/norestart') -Wait -PassThru
    $exitCode = $proc.ExitCode
    if ($exitCode -notin @(0, 1641, 3010)) {
        throw "msiexec exited with unexpected code $exitCode"
    }
} finally {
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}

$postInstallInfo = Get-Pdf24InstallInfo -AppId $appId
$postInstallVersion = if ($postInstallInfo) { $postInstallInfo.DisplayVersion } else { $null }
if ($null -eq $postInstallVersion -or [version]$postInstallVersion -lt [version]$latestVersion) {
    $foundDescription = if ($postInstallVersion) { $postInstallVersion } else { '<not found>' }
    [Console]::Error.WriteLine("Update of $appName did not succeed: expected version >= $latestVersion but found $foundDescription.")
    exit 1
}

[Console]::Out.WriteLine("$appName updated successfully to version $postInstallVersion.")
exit 0