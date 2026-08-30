<#
.SYNOPSIS
    Builds the release binary, bundles it with everything a brand-new install needs, and publishes
    that one bundle to the server.

.DESCRIPTION
    The counterpart to the macOS agent's packaging/publish-release.sh, and it does the same double
    duty: a human downloads the bundle from the Clients page for a fresh install, and an
    already-enrolled agent's own auto-update check downloads the very same file and just extracts
    the "kintsugi-agent.exe" entry out of it, ignoring the rest (see self_update.rs's extraction) —
    so there's only ever one artifact to build and publish, not two.

    The archive is a .tar.gz rather than a .zip, and that is load-bearing in two places: the server
    rewrites the archive's config.toml entry on every download (see AgentPackageArchiveRewriter,
    which reads gzip-tar specifically), and tar.exe has shipped in Windows since 10 1803 so
    extracting one needs nothing installed. Its top-level entry names matter too — self_update.rs
    looks for "kintsugi-agent.exe" by name.

    The bundled config.toml's enrollment_token is left blank on purpose: the server substitutes
    whatever AGENT_ENROLLMENT_TOKEN currently is on every download request, not just once at publish
    time, so a token rotation never makes an already-published package stale.

    The version published is always this crate's own Cargo.toml version — bump that first. Run from
    a plain (non-elevated) shell; unlike install.ps1 this never needs administrator rights, since
    it's talking to the server over the network rather than touching this machine.

.EXAMPLE
    .\publish-release.ps1

.EXAMPLE
    .\publish-release.ps1 -ApiBaseUrl 'https://kintsugi.example.com:8443' -ReleaseNotes 'Fixes the winget listing parser'
#>
[CmdletBinding()]
param(
    [string] $ApiBaseUrl = $(if ($env:AGENT_API_BASE_URL) { $env:AGENT_API_BASE_URL } else { 'https://kintsugi.example.com:8443' }),
    [string] $ReleaseNotes = ''
)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Split-Path -Parent $ScriptDir

$versionLine = Select-String -LiteralPath (Join-Path $ProjectDir 'Cargo.toml') -Pattern '^version\s*=\s*"([^"]+)"' | Select-Object -First 1
if (-not $versionLine) {
    throw "Could not read the version from $ProjectDir\Cargo.toml"
}
$Version = $versionLine.Matches[0].Groups[1].Value

Write-Host "Building kintsugi-agent v$Version (release)..."
Push-Location $ProjectDir
try {
    & cargo build --release
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

$BuiltBinary = Join-Path $ProjectDir 'target\release\kintsugi-agent.exe'
if (-not (Test-Path -LiteralPath $BuiltBinary)) {
    throw "Expected build output not found at $BuiltBinary"
}

$WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("kintsugi-publish-" + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

try {
    Copy-Item -LiteralPath $BuiltBinary -Destination (Join-Path $WorkDir 'kintsugi-agent.exe')
    foreach ($name in @('config.toml', 'install.ps1', 'uninstall.ps1')) {
        Copy-Item -LiteralPath (Join-Path $ScriptDir $name) -Destination (Join-Path $WorkDir $name)
    }

    $ArchiveName = "kintsugi-agent-windows-$Version.tar.gz"
    $ArchivePath = Join-Path $WorkDir $ArchiveName

    # -C plus bare filenames, not full source paths, so the archive's top-level entries are exactly
    # "kintsugi-agent.exe", "config.toml", and so on — what both install.ps1's own instructions and
    # self_update.rs's extraction expect, rather than being nested under a temp-directory path.
    & tar.exe -czf $ArchivePath -C $WorkDir kintsugi-agent.exe config.toml install.ps1 uninstall.ps1
    if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }

    Write-Host "Publishing $ArchiveName to $ApiBaseUrl..."

    # curl.exe, not `Invoke-RestMethod -Form`: that parameter only exists in PowerShell 6.1 and
    # later, and this script has to run under the Windows PowerShell 5.1 that every Windows machine
    # actually ships with. curl.exe has shipped in Windows since 10 1803, so this needs nothing
    # installed either — and it makes the invocation byte-for-byte the macOS publish script's.
    #
    # The platform is "windows" — the agent-package namespace, which is deliberately separate from
    # PlatformBucket's upgrade-path buckets on the server. self_update.rs asks for this same string.
    $body = & curl.exe --silent --show-error --write-out '\n%{http_code}' `
        -F 'platform=windows' `
        -F "version=$Version" `
        -F "releaseNotes=$ReleaseNotes" `
        -F "file=@$ArchivePath;filename=$ArchiveName" `
        ($ApiBaseUrl.TrimEnd('/') + '/api/agent-packages')
    if ($LASTEXITCODE -ne 0) { throw "curl failed with exit code $LASTEXITCODE" }

    $lines = @($body)
    $httpStatus = $lines[-1]
    $responseBody = ($lines[0..($lines.Count - 2)] -join "`n")
    if ($httpStatus -ne '200') {
        throw "Publish failed (HTTP $httpStatus): $responseBody"
    }

    Write-Host "Published: $responseBody"
} finally {
    Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
}
