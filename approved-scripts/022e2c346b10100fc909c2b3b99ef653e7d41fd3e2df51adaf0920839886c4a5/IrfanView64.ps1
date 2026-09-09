Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# WARNING: IrfanView is closed-source shareware (not hosted on GitHub/GitLab despite the
# candidate repos found during research - none of those repos are related to IrfanView).
# It has no versionless "latest" download URL and no JSON/API version endpoint. Its own
# built-in "Check for updates" feature just opens the download page in a browser. The
# latest-version check below scrapes the vendor's official 64-bit download page
# (https://www.irfanview.com/64bit.htm) for the encoded version in the installer
# filename (e.g. "iview475_x64.exe" -> "4.75"). This assumes IrfanView's long-standing
# version-encoding convention (single-digit major + 2-digit minor, e.g. 4.75 -> "475")
# continues to hold; if a future major version changes this encoding (e.g. a 2-digit
# major), the regex below will need updating. The installer itself is fetched from
# https://www.irfanview.info/files/iview<NNN>_x64.exe, the actual file host linked from
# the vendor's download page, using the version number discovered above (no stable
# "latest" alias exists for this file, unlike GitHub releases).

function Get-IrfanViewLatestVersion {
    $downloadPageUrl = 'https://www.irfanview.com/64bit.htm'
    $response = Invoke-WebRequest -Uri $downloadPageUrl -UseBasicParsing
    $match = [regex]::Match($response.Content, 'iview(\d{3})_x64\.exe')
    if (-not $match.Success) {
        throw "Could not find IrfanView 64-bit version marker on $downloadPageUrl"
    }
    $encoded = $match.Groups[1].Value
    $major = $encoded.Substring(0, 1)
    $minor = $encoded.Substring(1, 2)
    return "$major.$minor"
}

function Get-Argument {
    param([string[]]$ArgList)

    $result = @{
        AppName = $null
        AppId = $null
        UpdateVersion = $false
        Update = $false
    }

    $i = 0
    while ($i -lt $ArgList.Count) {
        switch ($ArgList[$i]) {
            '--appName' {
                if ($i + 1 -ge $ArgList.Count) { throw 'Missing value for --appName' }
                $result.AppName = $ArgList[$i + 1]
                $i += 2
            }
            '--appId' {
                if ($i + 1 -ge $ArgList.Count) { throw 'Missing value for --appId' }
                $result.AppId = $ArgList[$i + 1]
                $i += 2
            }
            '--update-version' {
                $result.UpdateVersion = $true
                $i += 1
            }
            '--update' {
                $result.Update = $true
                $i += 1
            }
            default {
                throw "Unknown argument: $($ArgList[$i])"
            }
        }
    }

    if (-not $result.AppName -or -not $result.AppId) {
        throw 'Missing --appName or --appId'
    }
    if ($result.UpdateVersion -eq $result.Update) {
        throw 'Exactly one of --update-version or --update is required'
    }

    return $result
}

$usage = 'Usage: script.ps1 --appName <name> --appId <id> (--update-version | --update)'

try {
    $parsed = Get-Argument -ArgList $args
}
catch {
    [Console]::Error.WriteLine($usage)
    exit 1
}

if ($parsed.UpdateVersion) {
    try {
        $latestVersion = Get-IrfanViewLatestVersion
        [Console]::Out.WriteLine($latestVersion)
        exit 0
    }
    catch {
        [Console]::Error.WriteLine("Failed to determine latest version: $_")
        exit 1
    }
}

# --update mode (runs on the managed Windows host as SYSTEM)

function Get-InstalledDisplayVersion {
    param([string]$AppId)

    $paths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$AppId",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
    )

    foreach ($path in $paths) {
        if (Test-Path -LiteralPath $path) {
            $key = Get-ItemProperty -LiteralPath $path
            return $key.DisplayVersion
        }
    }

    throw "Uninstall registry key for '$AppId' was not found; application is not installed or appId is wrong"
}

function Stop-IrfanViewProcess {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    param()

    $processName = 'i_view64'
    $proc = Get-Process -Name $processName -ErrorAction SilentlyContinue
    if (-not $proc) {
        return
    }

    if (-not $PSCmdlet.ShouldProcess($processName, 'Stop process')) {
        return
    }

    $proc | ForEach-Object { $_.CloseMainWindow() | Out-Null }

    $waited = 0
    while ($waited -lt 15) {
        Start-Sleep -Seconds 1
        $waited += 1
        $proc = Get-Process -Name $processName -ErrorAction SilentlyContinue
        if (-not $proc) {
            return
        }
    }

    Stop-Process -Name $processName -Force -ErrorAction SilentlyContinue
}

try {
    $latestVersion = Get-IrfanViewLatestVersion
}
catch {
    [Console]::Error.WriteLine("Failed to determine latest version: $_")
    exit 1
}

try {
    $installedVersion = Get-InstalledDisplayVersion -AppId $parsed.AppId
}
catch {
    [Console]::Error.WriteLine("$_")
    exit 1
}

if ([version]$installedVersion -ge [version]$latestVersion) {
    [Console]::Out.WriteLine("$($parsed.AppName) is already up to date (installed $installedVersion, latest $latestVersion).")
    exit 0
}

$encodedVersion = $latestVersion.Replace('.', '')
$downloadUrl = "https://www.irfanview.info/files/iview${encodedVersion}_x64.exe"

$workDir = Join-Path -Path $env:TEMP -ChildPath ("irfanview_update_" + [guid]::NewGuid().ToString('N'))
New-Item -Path $workDir -ItemType Directory -Force | Out-Null

try {
    Stop-IrfanViewProcess -Confirm:$false

    $installerPath = Join-Path -Path $workDir -ChildPath 'iview_x64_setup.exe'
    Invoke-WebRequest -Uri $downloadUrl -OutFile $installerPath -UseBasicParsing

    $proc = Start-Process -FilePath $installerPath -ArgumentList '/silent', '/allusers=1' -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        [Console]::Error.WriteLine("Installer exited with code $($proc.ExitCode)")
        exit 1
    }
}
finally {
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}

$finalVersion = Get-InstalledDisplayVersion -AppId $parsed.AppId
if ([version]$finalVersion -lt [version]$latestVersion) {
    [Console]::Error.WriteLine("Update did not take effect: installed version is still $finalVersion, expected at least $latestVersion")
    exit 1
}

[Console]::Out.WriteLine("$($parsed.AppName) updated successfully to version $finalVersion.")
exit 0