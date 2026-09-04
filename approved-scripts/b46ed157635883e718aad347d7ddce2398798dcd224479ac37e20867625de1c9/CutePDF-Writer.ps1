Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# WARNING: CutePDF Writer (Acro Software Inc.) is closed-source and has no public API,
# GitHub/GitLab releases page, RSS feed, or JSON version manifest. The vendor's own
# download page (cutepdf.com) is the only authoritative source found, and it only ever
# shows a coarse marketing version string ("Ver. 4.0") next to the CuteWriter.exe link;
# third-party trackers report finer internal build numbers (e.g. 4.0.1.5) that are not
# exposed anywhere on the page. This script scrapes that marketing version string via
# plain text processing (no JSON/API available), which is the best durable signal the
# vendor publishes. It may under-detect a build-only refresh that keeps the same "4.0"
# marketing version while still updating the binary at the stable download URL. The
# download URL itself (https://www.cutepdf.com/download/CuteWriter.exe) is version-less
# and always resolves to whatever build is currently live, so --update always installs
# the true latest binary regardless of what the scraped version string says; the scraped
# string is used only for the idempotency/comparison decision. This was verified via web
# research only (no live Windows host or pwsh execution) as of 2026-09-04.

$DownloadPageUrl = 'https://www.cutepdf.com/products/cutepdf/writer.asp'
$InstallerUrl = 'https://www.cutepdf.com/download/CuteWriter.exe'
$InstallerFileName = 'CuteWriter.exe'

function Write-UsageAndExit {
    [Console]::Error.WriteLine('Usage: script.ps1 --appName <name> --appId <id> (--update-version | --update)')
    exit 1
}

function Get-LatestCutePdfVersion {
    try {
        $response = Invoke-WebRequest -Uri $DownloadPageUrl -UseBasicParsing -TimeoutSec 30
    } catch {
        throw "Failed to fetch CutePDF Writer download page: $($_.Exception.Message)"
    }

    $content = $response.Content

    $mainIndex = $content.IndexOf('CuteWriter.exe', [System.StringComparison]::OrdinalIgnoreCase)
    if ($mainIndex -lt 0) {
        throw 'Could not locate the CuteWriter.exe download link on the vendor download page.'
    }
    $legacyIndex = $content.IndexOf('cutewriter32.exe', [System.StringComparison]::OrdinalIgnoreCase)
    $converterIndex = $content.IndexOf('converter.exe', [System.StringComparison]::OrdinalIgnoreCase)

    $versionMatches = [regex]::Matches($content, 'Ver\.?\s*(\d+(?:\.\d+){1,3})')
    if ($versionMatches.Count -eq 0) {
        throw 'Could not find any version string on the vendor download page.'
    }

    $best = $null
    $bestDistance = [int]::MaxValue
    foreach ($match in $versionMatches) {
        $distanceToMain = [Math]::Abs($match.Index - $mainIndex)
        $isClosestToMain = $true
        if ($legacyIndex -ge 0 -and [Math]::Abs($match.Index - $legacyIndex) -lt $distanceToMain) {
            $isClosestToMain = $false
        }
        if ($converterIndex -ge 0 -and [Math]::Abs($match.Index - $converterIndex) -lt $distanceToMain) {
            $isClosestToMain = $false
        }
        if ($isClosestToMain -and $distanceToMain -lt $bestDistance) {
            $bestDistance = $distanceToMain
            $best = $match
        }
    }

    if (-not $best) {
        throw 'Could not disambiguate the CutePDF Writer version string on the vendor download page.'
    }

    return $best.Groups[1].Value
}

function Get-InstalledCutePdfInfo {
    param(
        [Parameter(Mandatory = $true)][string] $AppId
    )

    $candidatePaths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$AppId",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
    )

    foreach ($path in $candidatePaths) {
        if (Test-Path -LiteralPath $path) {
            $props = Get-ItemProperty -LiteralPath $path
            return [pscustomobject]@{
                RegistryPath   = $path
                DisplayVersion = $props.DisplayVersion
            }
        }
    }

    throw "Registry uninstall key for '$AppId' was not found under HKLM Uninstall (native or WOW6432Node). The application does not appear to be installed."
}

function Close-CutePdfApplication {
    param(
        [Parameter(Mandatory = $true)][string] $AppName
    )

    $baseName = ($AppName -replace '\s+', '')
    $processNamePatterns = @('*CuteWriter*', '*CutePDF*', $baseName)

    $processes = @()
    foreach ($pattern in $processNamePatterns) {
        $processes += Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like $pattern }
    }
    $processes = $processes | Sort-Object -Property Id -Unique

    if (-not $processes) {
        return
    }

    foreach ($proc in $processes) {
        try {
            [void]$proc.CloseMainWindow()
        } catch {
            [Console]::Error.WriteLine("Warning: failed to request graceful close of process $($proc.Id): $($_.Exception.Message)")
        }
    }

    $waited = 0
    while ($waited -lt 15) {
        Start-Sleep -Seconds 1
        $waited++
        $processes = $processes | Where-Object { -not $_.HasExited }
        if (-not $processes) {
            break
        }
    }

    foreach ($proc in $processes) {
        if (-not $proc.HasExited) {
            try {
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            } catch {
                [Console]::Error.WriteLine("Warning: failed to force-stop process $($proc.Id): $($_.Exception.Message)")
            }
        }
    }
}

function Install-LatestCutePdf {
    $workDir = Join-Path -Path $env:TEMP -ChildPath ("cutepdf_update_" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $workDir -Force | Out-Null

    try {
        $installerPath = Join-Path -Path $workDir -ChildPath $InstallerFileName
        Invoke-WebRequest -Uri $InstallerUrl -OutFile $installerPath -UseBasicParsing -TimeoutSec 120

        $installArgs = @('/VERYSILENT', '/NORESTART', '/SP-', '/SUPPRESSMSGBOXES', '/NO3D')
        $proc = Start-Process -FilePath $installerPath -ArgumentList $installArgs -Wait -PassThru

        if ($proc.ExitCode -ne 0) {
            throw "CutePDF Writer installer exited with code $($proc.ExitCode)."
        }
    } finally {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ----- Argument parsing -----

$appName = $null
$appId = $null
$modeUpdateVersion = $false
$modeUpdate = $false

$i = 0
while ($i -lt $args.Count) {
    $arg = $args[$i]
    switch ($arg) {
        '--appName' {
            if ($i + 1 -ge $args.Count) { Write-UsageAndExit }
            $appName = $args[$i + 1]
            $i += 2
        }
        '--appId' {
            if ($i + 1 -ge $args.Count) { Write-UsageAndExit }
            $appId = $args[$i + 1]
            $i += 2
        }
        '--update-version' {
            $modeUpdateVersion = $true
            $i += 1
        }
        '--update' {
            $modeUpdate = $true
            $i += 1
        }
        default {
            Write-UsageAndExit
        }
    }
}

if (-not $appName -or -not $appId) { Write-UsageAndExit }
if ($modeUpdateVersion -eq $modeUpdate) { Write-UsageAndExit }

# ----- Mode execution -----

if ($modeUpdateVersion) {
    try {
        $latest = Get-LatestCutePdfVersion
        [Console]::Out.WriteLine($latest)
        exit 0
    } catch {
        [Console]::Error.WriteLine("Failed to determine latest version: $($_.Exception.Message)")
        exit 1
    }
}

if ($modeUpdate) {
    try {
        $latestVersionString = Get-LatestCutePdfVersion
    } catch {
        [Console]::Error.WriteLine("Failed to determine latest version: $($_.Exception.Message)")
        exit 1
    }

    try {
        $installedInfo = Get-InstalledCutePdfInfo -AppId $appId
    } catch {
        [Console]::Error.WriteLine($_.Exception.Message)
        exit 1
    }

    $latestVersion = [version]$latestVersionString
    $installedVersion = [version]$installedInfo.DisplayVersion

    if ($installedVersion -ge $latestVersion) {
        [Console]::Out.WriteLine("$appName is already up to date (installed $installedVersion, latest $latestVersion). No action taken.")
        exit 0
    }

    Close-CutePdfApplication -AppName $appName

    try {
        Install-LatestCutePdf
    } catch {
        [Console]::Error.WriteLine("Installation failed: $($_.Exception.Message)")
        exit 1
    }

    try {
        $postInstallInfo = Get-InstalledCutePdfInfo -AppId $appId
    } catch {
        [Console]::Error.WriteLine("Post-install verification failed: $($_.Exception.Message)")
        exit 1
    }

    $postInstallVersion = [version]$postInstallInfo.DisplayVersion
    if ($postInstallVersion -lt $latestVersion) {
        [Console]::Error.WriteLine("Update did not succeed: installed version is now $postInstallVersion, expected at least $latestVersion.")
        exit 1
    }

    [Console]::Out.WriteLine("$appName updated successfully to $postInstallVersion.")
    exit 0
}