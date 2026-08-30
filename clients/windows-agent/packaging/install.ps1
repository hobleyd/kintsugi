<#
.SYNOPSIS
    Installs kintsugi-agent as a Windows service plus a per-user logon task.

.DESCRIPTION
    The Windows counterpart to the macOS agent's packaging/install.sh, and deliberately shaped the
    same way: one privileged, machine-wide component that talks to the server (there a
    LaunchDaemon, here a service), and one unprivileged per-user component that owns the UI (there
    a LaunchAgent, here a logon-triggered scheduled task registered for the Users group).

    Uses the prebuilt "kintsugi-agent.exe" next to this script if present (i.e. when run from an
    extracted installer archive); otherwise builds from source with cargo, which requires Rust and
    this script to be run from a repo checkout at clients\windows-agent\packaging\install.ps1.

    The enrollment token is a rotating shared secret (see EnrollAgentCommandValidator /
    AGENT_ENROLLMENT_TOKEN on the server). This installer archive otherwise has no expiry and gets
    reused across many hosts and a long time, so the token deliberately isn't baked into it. Supply
    whatever the *current* token is at install time; omitting it falls back to whatever's in the
    packaged config.toml, which will fail enrollment with a clear "no enrollment token configured"
    error rather than silently sending a blank one.

    Note that a package downloaded from the server's own Clients page already has the current token
    substituted into its config.toml (see AgentPackageArchiveRewriter), so -EnrollmentToken is only
    needed when installing from an archive obtained some other way.

.EXAMPLE
    .\install.ps1

.EXAMPLE
    .\install.ps1 -EnrollmentToken '<current token>'
#>
[CmdletBinding()]
param(
    [string] $EnrollmentToken = $env:AGENT_ENROLLMENT_TOKEN
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must be run from an elevated PowerShell session (Run as Administrator).'
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Split-Path -Parent $ScriptDir

# Kept in step with clients\windows-agent\src\config.rs, which resolves these same paths at runtime.
$ServiceName = 'KintsugiAgent'
$TaskPath = '\Kintsugi\'
$TaskName = 'Kintsugi Agent UI'
$InstallDir = Join-Path $env:ProgramFiles 'Kintsugi'
$BinaryPath = Join-Path $InstallDir 'kintsugi-agent.exe'
$ConfigDir = Join-Path $env:ProgramData 'Kintsugi\kintsugi-agent'
$ConfigPath = Join-Path $ConfigDir 'config.toml'
$QueueDir = Join-Path $ConfigDir 'queue'
$IdentityDir = Join-Path $ConfigDir 'identity'

# ------------------------------------------------------------------------------------------------
# Binary
# ------------------------------------------------------------------------------------------------

$PrebuiltBinary = Join-Path $ScriptDir 'kintsugi-agent.exe'
if (Test-Path -LiteralPath $PrebuiltBinary) {
    Write-Host "Using prebuilt binary at $PrebuiltBinary..."
    $SourceBinary = $PrebuiltBinary
} else {
    Write-Host 'No prebuilt binary found; building from source (release)...'
    Push-Location $ProjectDir
    try {
        & cargo build --release
        if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit code $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
    $SourceBinary = Join-Path $ProjectDir 'target\release\kintsugi-agent.exe'
}

Write-Host "Installing binary to $BinaryPath..."
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

# The service holds its own image locked, so a reinstall over a running service can't just copy over
# the top. Stopping first is enough — unlike self_update.rs, which has to move the running binary
# aside because it *is* the running binary.
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host '  stopping the existing service first...'
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
}
Copy-Item -LiteralPath $SourceBinary -Destination $BinaryPath -Force

# A file downloaded through a browser carries a Zone.Identifier alternate data stream marking it as
# from the internet. This binary isn't signed, so a marked copy can be blocked outright by
# SmartScreen or an AppLocker policy — and when that happens the service simply fails to start with
# no output anywhere, which looks exactly like "nothing happened". Clearing it here means the
# install itself is the one moment this is guaranteed to be dealt with. (The direct counterpart to
# install.sh's `xattr -dr com.apple.quarantine`.)
Unblock-File -LiteralPath $BinaryPath -ErrorAction SilentlyContinue

# ------------------------------------------------------------------------------------------------
# Configuration and the directories the two halves share
# ------------------------------------------------------------------------------------------------

Write-Host "Installing config to $ConfigPath..."
New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null

# Always overwritten, not preserved across reinstalls: this is a centrally-managed fleet agent, not
# something a user configures by hand — the server is the single source of truth, and the packaged
# config.toml is the current source of truth for the little that's left (api_base_url, the
# enrollment token). A stale local override surviving a reinstall would just be a silent way for a
# host to drift from that. The enrollment token itself is one-time-use (see identity.rs) — once a
# host is enrolled, this file being reset to defaults on every reinstall costs nothing.
Copy-Item -LiteralPath (Join-Path $ScriptDir 'config.toml') -Destination $ConfigPath -Force

if ($EnrollmentToken) {
    # Rewritten by dropping any existing line and appending the current value, rather than by
    # substitution: the token is a secret whose content this script doesn't control, and a regex
    # replacement would break (or need fragile escaping) if it happened to contain a special
    # character. TOML's own escaping only needs backslashes and double quotes handled for a basic
    # string.
    $escaped = $EnrollmentToken.Replace('\', '\\').Replace('"', '\"')
    $kept = Get-Content -LiteralPath $ConfigPath | Where-Object { $_ -notmatch '^\s*enrollment_token' }
    $lines = @($kept) + "enrollment_token = `"$escaped`""

    # Written via .NET with an explicitly BOM-less UTF-8 encoder rather than `Set-Content -Encoding
    # UTF8`, which in Windows PowerShell 5.1 means UTF-8 *with* a byte-order mark. A BOM here is not
    # cosmetic: config.rs parses this file with the `toml` crate, which rejects the leading U+FEFF —
    # and Config::load_from swallows a parse failure and falls back to built-in defaults, so the
    # symptom would be an agent silently ignoring both api_base_url and the token that was just set.
    [System.IO.File]::WriteAllLines($ConfigPath, $lines, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host '  enrollment token set from the command line/environment.'
} elseif (-not (Select-String -LiteralPath $ConfigPath -Pattern '^enrollment_token = "[^"]' -Quiet)) {
    Write-Warning 'No enrollment token supplied (-EnrollmentToken / $env:AGENT_ENROLLMENT_TOKEN) and the'
    Write-Warning '  packaged config.toml''s own enrollment_token is blank. Enrollment will fail until this'
    Write-Warning "  host's config.toml has the current token — see $ConfigDir\service.log."
}

# The handoff directory between the SYSTEM service and the per-user tray process: everything the
# tray process is deliberately not privileged enough to do itself goes through here (see
# src\queue.rs). Users need Modify so a logged-in user can drop a request; only the service ever
# acts on one. This is the direct counterpart to install.sh's `root:admin 0770` on its own queue.
Write-Host "Creating queue directory at $QueueDir..."
New-Item -ItemType Directory -Path $QueueDir -Force | Out-Null
# SIDs, not names: "Users" is localized on a non-English Windows install and S-1-5-32-545 never is.
& icacls.exe $QueueDir /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null

# This host's mutual-TLS identity. Created here so it exists with the right owner before the service
# first runs; the agent itself locks it down to SYSTEM and Administrators the moment it writes a
# private key into it (see identity.rs's restrict_identity_permissions). Unlike macOS — where the
# per-user process reads this same identity and the directory has to stay group-readable — nothing
# unprivileged ever opens this on Windows.
Write-Host "Creating identity directory at $IdentityDir..."
New-Item -ItemType Directory -Path $IdentityDir -Force | Out-Null

# ------------------------------------------------------------------------------------------------
# The service
# ------------------------------------------------------------------------------------------------

Write-Host "Registering the $ServiceName service..."
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    # A reinstall: the binary path or account may have changed, so update rather than assume.
    & sc.exe config $ServiceName binPath= "`"$BinaryPath`"" start= delayed-auto obj= LocalSystem | Out-Null
} else {
    New-Service -Name $ServiceName `
        -BinaryPathName "`"$BinaryPath`"" `
        -DisplayName 'Kintsugi Patching Agent' `
        -Description 'Registers this PC with the Kintsugi patching system, reports installed applications, and applies approved updates.' `
        -StartupType Automatic | Out-Null
    # Delayed start: this service's first act is a network round trip, and starting after the
    # network stack has settled avoids a boot-time failure and retry on every single startup.
    & sc.exe config $ServiceName start= delayed-auto | Out-Null
}

# Restart on failure rather than staying dead. The macOS daemon gets this for free — launchd
# re-invokes it on a schedule regardless of how the last run ended — whereas a Windows service that
# crashes stays stopped until someone notices, which for a fleet agent means a host silently
# dropping out of inventory.
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null

# ------------------------------------------------------------------------------------------------
# The per-user tray process
# ------------------------------------------------------------------------------------------------

Write-Host "Registering the '$TaskName' logon task..."
$action = New-ScheduledTaskAction -Execute $BinaryPath -Argument '--agent'
$trigger = New-ScheduledTaskTrigger -AtLogOn
# The Users group, not a specific account: one machine-wide task definition that launches once per
# user, in that user's own session and at that user's own privilege level — which is exactly what
# /Library/LaunchAgents gives the macOS agent.
$principal = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -DontStopOnIdleEnd `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit ([TimeSpan]::Zero)
# ExecutionTimeLimit of zero means "never stop it": this process is meant to run for as long as the
# user is logged in, and the default three-day limit would silently kill the tray icon on any
# machine nobody signs out of.

Unregister-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName `
    -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description 'Shows Kintsugi patching status and prompts, and drives the patch cycle for the logged-in user.' | Out-Null

# ------------------------------------------------------------------------------------------------
# First run
# ------------------------------------------------------------------------------------------------

# One check-in synchronously, before starting the service, so this script's own output reports
# whether enrollment and registration actually worked — rather than leaving an administrator to go
# hunting in a log to find out. This is exactly what --check-in exists for.
Write-Host 'Running the first check-in (enrollment and registration)...'
& $BinaryPath --check-in
if ($LASTEXITCODE -ne 0) {
    Write-Warning "The first check-in failed (exit code $LASTEXITCODE). The service is still being started and"
    Write-Warning "  will retry hourly. See $ConfigDir\service.log for the reason — the most common one is a"
    Write-Warning '  missing or rotated enrollment token.'
}

Write-Host 'Starting the service...'
Start-Service -Name $ServiceName

# /Library/LaunchAgents is auto-loaded for every new login session; a logon-triggered task is the
# same, but neither fires for a user who is *already* logged in right now. Starting it explicitly
# means a reinstall/upgrade doesn't require a sign out/in to take effect.
$consoleUser = (Get-CimInstance -ClassName Win32_ComputerSystem).UserName
if ($consoleUser) {
    Write-Host "Starting the tray agent in $consoleUser's session..."
    Start-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction SilentlyContinue
} else {
    Write-Host 'No user is currently logged in; the tray agent will start at next logon.'
}

Write-Host ''
Write-Host 'Installed and started. Registration ran just now, then runs hourly at a check-in minute'
Write-Host 'the service assigns itself on first run (see the log below).'
Write-Host "Service log:        $ConfigDir\service.log"
Write-Host 'Per-user tray log:  %LOCALAPPDATA%\Kintsugi\kintsugi-agent\agent.log'
