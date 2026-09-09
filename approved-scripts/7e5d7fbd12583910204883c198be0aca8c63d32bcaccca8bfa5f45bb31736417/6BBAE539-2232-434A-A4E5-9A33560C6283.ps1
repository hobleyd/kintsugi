Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# WARNING: Google Drive for desktop is closed-source and distributed only from Google's own
# servers (no GitHub/GitLab release catalog -- the repos surfaced by the identifier/name search
# were unrelated projects). There is no versioned JSON/REST API for its release history, so the
# latest-version check scrapes the first "Version X.Y[.Z[.W]]" token out of Google's own published
# release-notes article (support.google.com/a/answer/7577057, which 301-redirects to
# knowledge.workspace.google.com -- Invoke-WebRequest follows this automatically). That article
# lists entries newest-first and, per Google's own admin documentation, only publishes a version
# once it is 100% rolled out, so the first match is the current stable release. Google's release
# notes commonly show a 2- or 3-part number (e.g. "131.0") while the installed DisplayVersion is
# 4-part (e.g. "131.0.2.0"); this is handled by casting both to [version], under which a shorter
# number's missing Build/Revision fields default to -1, so any real 4-part release of the same
# major.minor still compares as ">=" the notes value. The one gap this can't detect is a same-
# major.minor hotfix that never gets its own release-notes line -- accept auto-update as ground
# truth for that rare case.

function Get-LatestVersion {
    $uri = 'https://support.google.com/a/answer/7577057?hl=en'
    try {
        $response = Invoke-WebRequest -Uri $uri -UseBasicParsing
    } catch {
        throw "Unable to fetch Google Drive release notes from $uri : $_"
    }
    $match = [regex]::Match($response.Content, 'Version\s+(\d+\.\d+(?:\.\d+){0,2})')
    if (-not $match.Success) {
        throw "Could not find a version number in the Google Drive release notes page ($uri)."
    }
    return $match.Groups[1].Value
}

function Get-InstalledVersion {
    param([string]$AppId, [string]$AppName)

    $candidatePaths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$AppId",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
    )
    $key = $candidatePaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $key) {
        throw "$AppName (app id $AppId) is not installed: no uninstall registry key found."
    }
    $displayVersion = (Get-ItemProperty -LiteralPath $key -Name 'DisplayVersion').DisplayVersion
    if ([string]::IsNullOrWhiteSpace($displayVersion)) {
        throw "$AppName (app id $AppId) uninstall key at $key has no DisplayVersion."
    }
    return $displayVersion
}

function Close-RunningApp {
    $processName = 'GoogleDriveFS'
    $proc = Get-Process -Name $processName -ErrorAction SilentlyContinue
    if (-not $proc) {
        return
    }
    $proc | ForEach-Object { $null = $_.CloseMainWindow() }
    $waited = 0
    while ((Get-Process -Name $processName -ErrorAction SilentlyContinue) -and $waited -lt 15) {
        Start-Sleep -Seconds 1
        $waited++
    }
    $stillRunning = Get-Process -Name $processName -ErrorAction SilentlyContinue
    if ($stillRunning) {
        $stillRunning | Stop-Process -Force
    }
}

function Install-LatestVersion {
    $downloadUri = 'https://dl.google.com/drive-file-stream/GoogleDriveSetup.exe'
    $workDir = Join-Path -Path $env:TEMP -ChildPath ([System.Guid]::NewGuid().ToString())
    New-Item -ItemType Directory -Path $workDir | Out-Null
    try {
        $installerPath = Join-Path -Path $workDir -ChildPath 'GoogleDriveSetup.exe'
        Invoke-WebRequest -Uri $downloadUri -OutFile $installerPath -UseBasicParsing

        $proc = Start-Process -FilePath $installerPath -ArgumentList '--silent', '--skip_launch_new' -Wait -PassThru
        if ($proc.ExitCode -ne 0) {
            throw "GoogleDriveSetup.exe exited with code $($proc.ExitCode)."
        }
    } finally {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Test-VersionAtLeast {
    param([string]$Installed, [string]$Latest)
    return ([version]$Installed) -ge ([version]$Latest)
}

# --- argument parsing ---

$usage = 'Usage: script.ps1 --appName <name> --appId <id> (--update-version | --update)'

$appName = $null
$appId = $null
$updateVersion = $false
$update = $false

$i = 0
while ($i -lt $args.Count) {
    switch ($args[$i]) {
        '--appName' {
            if ($i + 1 -ge $args.Count) { [Console]::Error.WriteLine($usage); exit 1 }
            $appName = $args[$i + 1]
            $i += 2
        }
        '--appId' {
            if ($i + 1 -ge $args.Count) { [Console]::Error.WriteLine($usage); exit 1 }
            $appId = $args[$i + 1]
            $i += 2
        }
        '--update-version' {
            $updateVersion = $true
            $i += 1
        }
        '--update' {
            $update = $true
            $i += 1
        }
        default {
            [Console]::Error.WriteLine($usage)
            exit 1
        }
    }
}

if ([string]::IsNullOrWhiteSpace($appName) -or [string]::IsNullOrWhiteSpace($appId)) {
    [Console]::Error.WriteLine($usage)
    exit 1
}
if ($updateVersion -eq $update) {
    # true only when both or neither were supplied
    [Console]::Error.WriteLine($usage)
    exit 1
}

# --- mode: --update-version ---

if ($updateVersion) {
    try {
        $latest = Get-LatestVersion
        [Console]::Out.WriteLine($latest)
        exit 0
    } catch {
        [Console]::Error.WriteLine("Failed to determine latest version of ${appName}: $_")
        exit 1
    }
}

# --- mode: --update ---

try {
    $latest = Get-LatestVersion
} catch {
    [Console]::Error.WriteLine("Failed to determine latest version of ${appName}: $_")
    exit 1
}

try {
    $installed = Get-InstalledVersion -AppId $appId -AppName $appName
} catch {
    [Console]::Error.WriteLine("$_")
    exit 1
}

if (Test-VersionAtLeast -Installed $installed -Latest $latest) {
    [Console]::Out.WriteLine("$appName is already up to date (installed $installed, latest $latest).")
    exit 0
}

try {
    Close-RunningApp
    Install-LatestVersion
} catch {
    [Console]::Error.WriteLine("Failed to update ${appName}: $_")
    exit 1
}

try {
    $postInstall = Get-InstalledVersion -AppId $appId -AppName $appName
} catch {
    [Console]::Error.WriteLine("$_")
    exit 1
}

if (-not (Test-VersionAtLeast -Installed $postInstall -Latest $latest)) {
    [Console]::Error.WriteLine("Update failed verification: installed version is $postInstall, expected at least $latest.")
    exit 1
}

[Console]::Out.WriteLine("$appName updated successfully to $postInstall.")
exit 0