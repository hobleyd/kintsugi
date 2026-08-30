<#
.SYNOPSIS
    Stops and removes kintsugi-agent's service, logon task, and binary.

.DESCRIPTION
    The counterpart to packaging/uninstall.sh on macOS, and deliberately just as conservative: it
    removes what this installer put in place but leaves the configuration and each user's own state
    behind for a human to clean up. A *complete* removal — config, identity, per-user state, the lot
    — is what the agent performs on itself when the server marks this host for removal (see
    src\self_removal.rs); this script is the local, manual equivalent and shouldn't silently discard
    an enrolled identity someone may want to keep.

.EXAMPLE
    .\uninstall.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must be run from an elevated PowerShell session (Run as Administrator).'
}

$ServiceName = 'KintsugiAgent'
$TaskPath = '\Kintsugi\'
$TaskName = 'Kintsugi Agent UI'
$InstallDir = Join-Path $env:ProgramFiles 'Kintsugi'
$BinaryPath = Join-Path $InstallDir 'kintsugi-agent.exe'
$ConfigDir = Join-Path $env:ProgramData 'Kintsugi\kintsugi-agent'

Write-Host 'Stopping and removing the per-user tray task...'
Stop-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

Write-Host 'Stopping and removing the service...'
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
    # sc.exe marks the service for deletion; the registration only actually disappears once every
    # handle to it is closed. Nothing here depends on that having happened, but a moment's pause
    # means the binary below is no longer locked by a process that is still winding down.
    Start-Sleep -Seconds 2
}

Write-Host 'Removing the binary...'
Remove-Item -LiteralPath $BinaryPath -Force -ErrorAction SilentlyContinue
# Left behind by a self-update that hasn't been through a service restart yet — see
# self_update.rs's replace_running_binary.
Remove-Item -LiteralPath "$BinaryPath.old" -Force -ErrorAction SilentlyContinue

# Only if nothing else lives there, so a machine that keeps other Kintsugi tooling under
# %ProgramFiles%\Kintsugi doesn't lose it to this agent's uninstall.
if (Test-Path -LiteralPath $InstallDir) {
    if (-not (Get-ChildItem -LiteralPath $InstallDir -Force)) {
        Remove-Item -LiteralPath $InstallDir -Force
    }
}

Write-Host ''
Write-Host 'Removed the service, the logon task, and the binary.'
Write-Host "Config, identity, queue, and logs left in place at: $ConfigDir"
Write-Host '  (remove manually if no longer needed — note this includes the enrolled mutual-TLS'
Write-Host '   identity, which the host would have to re-enroll without.)'
Write-Host 'Per-user schedule state left in place under each user''s'
Write-Host '  %LOCALAPPDATA%\Kintsugi (remove manually if no longer needed).'
