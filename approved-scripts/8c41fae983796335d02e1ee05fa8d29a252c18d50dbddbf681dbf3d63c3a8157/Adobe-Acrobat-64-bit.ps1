Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
# WARNING: Adobe Acrobat is closed-source and has no GitHub/GitLab-style releases API or
# stable "latest" JSON endpoint. Research (2026-09-04) found that appId
# {AC76BA86-1033-1033-7760-BC15014EA700} is Adobe's product code for the unified 64-bit
# "Adobe Acrobat (64-bit)" application (installs as the free Reader engine, upgrading in
# place to full Acrobat if licensed); its process is Acrobat.exe under
# "Program Files\Adobe\Acrobat DC\Acrobat\". Latest-version detection uses Adobe's own
# enterprise release notes page (Continuous Track), which listed 26.002.21869 as the
# newest entry at research time, matching the fleet's installed version exactly. Adobe's
# rdc.adobe.io backend API (used by get.adobe.com) was also checked but returned a stale
# version (26.001.21771) at the same time, so it was rejected as unreliable in favor of
# the release notes page. The full-installer download URL
# (ardownload2.adobe.com/pub/adobe/acrobat/win/AcrobatDC/<version>/AcroRdrDCx64<version>_en_US.exe)
# was confirmed live for the current version, but embeds the version number (Adobe has no
# version-independent "latest" alias like GitHub does) and assumes an English-language
# fleet -- adjust the "en_US" literal below if managing non-English hosts.

function Write-UsageAndExit {
    [Console]::Error.WriteLine('Usage: script.ps1 --appName <name> --appId <id> (--update-version | --update)')
    exit 2
}

function Get-LatestAcrobatVersion {
    $uri = 'https://www.adobe.com/devnet-docs/acrobatetk/tools/ReleaseNotesDC/index.html?PID=3987385'
    $response = Invoke-WebRequest -Uri $uri
    $match = [regex]::Match($response.Content, '\b\d{2}\.\d{3}\.\d{5}\b')
    if (-not $match.Success) {
        throw 'Could not locate a version number on the Adobe Acrobat release notes page.'
    }
    return $match.Value
}

function Get-AcrobatUninstallKeyPath {
    param([string]$AppId)

    $candidates = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$AppId",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }
    return $null
}

function Get-AcrobatInstalledVersion {
    param([string]$AppId)

    $keyPath = Get-AcrobatUninstallKeyPath -AppId $AppId
    if (-not $keyPath) {
        throw "No uninstall registry key found for appId '$AppId'. The application does not appear to be installed."
    }
    $properties = Get-ItemProperty -LiteralPath $keyPath
    if (-not $properties.PSObject.Properties['DisplayVersion'] -or [string]::IsNullOrWhiteSpace($properties.DisplayVersion)) {
        throw "Uninstall registry key '$keyPath' has no DisplayVersion value."
    }
    return $properties.DisplayVersion
}

function Stop-AcrobatProcess {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    param()

    $processes = Get-Process -Name 'Acrobat' -ErrorAction SilentlyContinue
    if (-not $processes) {
        return
    }
    foreach ($process in $processes) {
        if ($PSCmdlet.ShouldProcess("Acrobat.exe (PID $($process.Id))", 'Close main window')) {
            [void]$process.CloseMainWindow()
        }
    }
    $waited = 0
    while ($waited -lt 15) {
        Start-Sleep -Seconds 1
        $waited++
        $processes = Get-Process -Name 'Acrobat' -ErrorAction SilentlyContinue
        if (-not $processes) {
            return
        }
    }
    foreach ($process in $processes) {
        if ($PSCmdlet.ShouldProcess("Acrobat.exe (PID $($process.Id))", 'Stop process')) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

function Install-LatestAcrobat {
    param([string]$LatestVersion)

    $versionNoDots = $LatestVersion.Replace('.', '')
    $downloadUri = "https://ardownload2.adobe.com/pub/adobe/acrobat/win/AcrobatDC/$versionNoDots/AcroRdrDCx64${versionNoDots}_en_US.exe"
    $tempDir = Join-Path -Path $env:TEMP -ChildPath ('AcrobatUpdate_' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDir | Out-Null
    try {
        $installerPath = Join-Path -Path $tempDir -ChildPath 'AcroRdrDCx64.exe'
        Invoke-WebRequest -Uri $downloadUri -OutFile $installerPath

        Stop-AcrobatProcess

        $process = Start-Process -FilePath $installerPath -ArgumentList '/sAll', '/rs', '/msi', 'EULA_ACCEPT=YES' -Wait -PassThru
        if ($process.ExitCode -ne 0 -and $process.ExitCode -ne 3010) {
            throw "Acrobat installer exited with code $($process.ExitCode)."
        }
    } finally {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$appName = $null
$appId = $null
$updateVersionMode = $false
$updateMode = $false

$argCount = $args.Count
$i = 0
while ($i -lt $argCount) {
    switch ($args[$i]) {
        '--appName' {
            if ($i + 1 -ge $argCount) { Write-UsageAndExit }
            $appName = $args[$i + 1]
            $i += 2
        }
        '--appId' {
            if ($i + 1 -ge $argCount) { Write-UsageAndExit }
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
        default { Write-UsageAndExit }
    }
}

if ([string]::IsNullOrWhiteSpace($appName) -or [string]::IsNullOrWhiteSpace($appId)) {
    Write-UsageAndExit
}
if ($updateVersionMode -eq $updateMode) {
    Write-UsageAndExit
}

if ($updateVersionMode) {
    try {
        $version = Get-LatestAcrobatVersion
    } catch {
        [Console]::Error.WriteLine("Failed to determine latest Adobe Acrobat version: $($_.Exception.Message)")
        exit 1
    }
    [Console]::Out.WriteLine($version)
    exit 0
}

try {
    $latestVersion = Get-LatestAcrobatVersion
    $installedVersion = Get-AcrobatInstalledVersion -AppId $appId

    if ([version]$installedVersion -ge [version]$latestVersion) {
        [Console]::Out.WriteLine("$appName is already up to date (installed $installedVersion, latest $latestVersion).")
        exit 0
    }

    [Console]::Out.WriteLine("Updating $appName from $installedVersion to $latestVersion.")
    Install-LatestAcrobat -LatestVersion $latestVersion

    $postInstallVersion = Get-AcrobatInstalledVersion -AppId $appId
    if ([version]$postInstallVersion -lt [version]$latestVersion) {
        [Console]::Error.WriteLine("Update reported success but installed version is still $postInstallVersion (expected at least $latestVersion).")
        exit 1
    }

    [Console]::Out.WriteLine("$appName updated successfully to $postInstallVersion.")
    exit 0
} catch {
    [Console]::Error.WriteLine("Failed to update ${appName}: $($_.Exception.Message)")
    exit 1
}