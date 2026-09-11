Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# WARNING: Microsoft publishes no versioned feed, API, or JSON manifest for the
# "Visual C++ v14 Redistributable" itself -- its own docs
# (learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) explicitly say the
# only way to learn the current version is to download the permalink installer and read
# its file properties, precisely because it updates frequently with no version in the
# page or in the download URL. The best available live, text-only source found is the
# version-numbered manifest directories Microsoft maintains in its own winget-pkgs
# GitHub repository (github.com/microsoft/winget-pkgs), queried here via the GitHub
# Contents API. That API is subject to GitHub's unauthenticated rate limit (60
# requests/hour per source IP) -- acceptable for periodic fleet checks, but something to
# be aware of if this is invoked very frequently from a single address. Installation
# itself uses Microsoft's own documented permanent redirect link, which needs no version
# number and will keep resolving to whatever is current indefinitely.

function Show-Usage {
    [Console]::Error.WriteLine('Usage: script.ps1 --appName <name> --appId <id> (--update-version | --update)')
}

function Get-LatestVersion {
    $uri = 'https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/m/Microsoft/VCRedist/2015%2B/x64'
    $headers = @{
        'User-Agent' = 'fleet-update-checker'
        'Accept'     = 'application/vnd.github+json'
    }
    $entries = Invoke-RestMethod -Uri $uri -Headers $headers -Method Get
    $versionPattern = '^\d+(\.\d+){3}$'
    $versions = $entries |
        Where-Object { $_.type -eq 'dir' -and $_.name -match $versionPattern } |
        ForEach-Object { [version]$_.name } |
        Sort-Object -Descending
    if (-not $versions -or $versions.Count -eq 0) {
        throw 'No version-formatted entries found in the winget-pkgs VCRedist manifest listing.'
    }
    return $versions[0].ToString()
}

function Get-InstalledVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AppId
    )
    $candidatePaths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$AppId",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
    )
    foreach ($path in $candidatePaths) {
        if (Test-Path -LiteralPath $path) {
            $props = Get-ItemProperty -LiteralPath $path -Name DisplayVersion -ErrorAction Stop
            return $props.DisplayVersion
        }
    }
    throw "No uninstall registry key found for app id '$AppId' under either the native or WOW6432Node Uninstall hive. The application is either not installed on this host or the app id is wrong."
}

$AppName = $null
$AppId = $null
$updateVersionMode = $false
$updateMode = $false

$i = 0
while ($i -lt $args.Count) {
    $token = $args[$i]
    switch ($token) {
        '--appName' {
            if ($i + 1 -ge $args.Count) { Show-Usage; exit 1 }
            $AppName = $args[$i + 1]
            $i += 2
        }
        '--appId' {
            if ($i + 1 -ge $args.Count) { Show-Usage; exit 1 }
            $AppId = $args[$i + 1]
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
            Show-Usage
            exit 1
        }
    }
}

if ([string]::IsNullOrWhiteSpace($AppName) -or [string]::IsNullOrWhiteSpace($AppId) -or ($updateVersionMode -eq $updateMode)) {
    Show-Usage
    exit 1
}

if ($updateVersionMode) {
    try {
        $latest = Get-LatestVersion
        [Console]::Out.WriteLine($latest)
        exit 0
    } catch {
        [Console]::Error.WriteLine("Failed to determine latest version for '$AppName': $($_.Exception.Message)")
        exit 1
    }
}

# --update mode: runs on the managed Windows host itself.
try {
    $latestVersion = Get-LatestVersion
    $installedVersion = Get-InstalledVersion -AppId $AppId

    if ([version]$installedVersion -ge [version]$latestVersion) {
        [Console]::Out.WriteLine("'$AppName' is already at or above the latest version ($installedVersion >= $latestVersion). No action taken.")
        exit 0
    }

    # The VC++ Redistributable installs shared runtime libraries only; it has no
    # associated running application/process to close before an in-place upgrade.

    $workDir = Join-Path -Path $env:TEMP -ChildPath ("vcredist_update_" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $workDir | Out-Null
    try {
        $installerPath = Join-Path -Path $workDir -ChildPath 'VC_redist.x64.exe'
        # Documented permanent link (see learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)
        # that always resolves to whatever the current supported x64 build is.
        Invoke-WebRequest -Uri 'https://aka.ms/vc14/vc_redist.x64.exe' -OutFile $installerPath -UseBasicParsing

        $proc = Start-Process -FilePath $installerPath -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
        if ($proc.ExitCode -notin 0, 1641, 3010) {
            throw "Installer exited with unexpected code $($proc.ExitCode)."
        }
    } finally {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    $finalVersion = Get-InstalledVersion -AppId $AppId
    if ([version]$finalVersion -lt [version]$latestVersion) {
        [Console]::Error.WriteLine("Update of '$AppName' did not reach the latest version: installed version is now $finalVersion, expected at least $latestVersion.")
        exit 1
    }

    [Console]::Out.WriteLine("Successfully updated '$AppName' to version $finalVersion.")
    exit 0
} catch {
    [Console]::Error.WriteLine("Update of '$AppName' failed: $($_.Exception.Message)")
    exit 1
}