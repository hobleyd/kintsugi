using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.AgentPackages;

/// <summary>
/// The silent PowerShell installer an administrator pastes into CrowdStrike Falcon (Real Time
/// Response, or Falcon for IT) to install the Windows agent across a fleet — the deployment shape
/// for an estate where an operator downloading a tarball from this server's Clients screen and
/// running it by hand on each host is not how software arrives.
/// </summary>
/// <remarks>
/// <para>
/// <b>The pinned checksum is the whole security design, and it only works because of how the script
/// travels.</b> The script downloads the release archive from GitHub rather than from this server,
/// so nothing on this server terminates that connection and nothing about it can be authenticated
/// the way an enrolled agent's requests are. What vouches for the bytes is the SHA-256 written into
/// the script text below, which the server took over the archive it fetched from GitHub itself (see
/// <see cref="AgentPackage.UpstreamSha256"/>). The script reaches the host through CrowdStrike — a
/// separately authenticated management channel — so that literal is a trust anchor entirely
/// independent of the TLS connection to GitHub. That is what makes this resistant to the realistic
/// interception case in an estate that runs CrowdStrike at all: a corporate TLS-inspecting proxy,
/// which holds a certificate the host's own trust store accepts and against which TLS alone proves
/// nothing.
/// </para>
/// <para>
/// The hash must therefore never be something the administrator types. A hand-transcribed pin fails
/// closed on a wrong paste, and the natural fix for that failure — re-deriving the hash from
/// whatever actually downloaded — is not a control at all. It is rendered in, along with
/// <c>api_base_url</c> and the current enrollment token, so all three are right by construction.
/// </para>
/// <para>
/// <b>What this does not prove.</b> The pin is only as good as this server's own fetch of the
/// archive at import time, which went over ordinary TLS to GitHub. That is one fetch at a
/// controlled point rather than one per endpoint behind whatever proxy each site runs, which is the
/// improvement; it is not an attestation. Authenticode would be the real answer and is unavailable:
/// nothing signs the Windows binary today (see the note in
/// <c>packaging/kintsugi-remote-control.mobileconfig.example</c> on the equivalent macOS gap). The
/// verification below is written so a signature check can be added beside it rather than in place
/// of it.
/// </para>
/// <para>
/// <b>The script contains a live secret.</b> The enrollment token is rendered into it, exactly as
/// it is substituted into every archive downloaded from the Clients screen (see
/// <c>IAgentPackageArchiveRewriter</c>). It is one-time-use per host and rotatable, but the script
/// text is still a credential and the route that produces it carries
/// <c>[RequireAdminSession]</c> for that reason.
/// </para>
/// <para>
/// <b>It installs; it does not upgrade.</b> The pinned version goes stale the moment the fleet
/// self-updates past it from this server, and that is correct — the agent's own
/// <c>self_update</c> owns upgrades and takes them from here, not from GitHub. So a re-run against
/// an already-installed host is a no-op by default. Re-render the script when the pinned build is
/// too old to be worth handing to a brand-new host.
/// </para>
/// <para>
/// <b>The archive's own <c>install.ps1</c> does the installing.</b> Reimplementing it here would
/// duplicate three things that are load-bearing and quiet when wrong: <c>obj= LocalSystem</c> on
/// the service (which the identity directory's ACL needs, and which <c>SE_TCB_NAME</c> — and so
/// remote control's session helper — needs as well), the queue directory's ACL granted by SID
/// rather than by the localized name "Users", and the BOM-less UTF-8 write of <c>config.toml</c>
/// without which <c>Config::load_from</c> silently falls back to built-in defaults. Delegating also
/// guarantees the installer that runs is the one shipped with the binary it is installing.
/// </para>
/// <para>
/// Windows only, by nature — the three agents are otherwise kept deliberately in step. CrowdStrike
/// is the reason this platform needs it: there is no equivalent ask for macOS or Linux yet, and
/// inventing one would be two more texts with no deployment behind them.
/// </para>
/// </remarks>
public static class WindowsBootstrapScript
{
    /// <summary>Rendered when <see cref="AgentPackage.UpstreamSha256"/> is null — a package
    /// published by a release script rather than imported, or imported before the pin was recorded.
    /// Surfaced as a reason rather than an empty script, because "no script" and "a script with
    /// nothing pinned in it" must never look the same to a reader.</summary>
    public const string NoUpstreamProvenanceReason =
        "This Windows package carries no upstream checksum, so a deployment script cannot pin what it "
        + "downloads. Press \"Refresh clients\" to record one from the release it came from.";

    /// <summary>
    /// Renders the script for one published Windows package.
    /// </summary>
    /// <param name="package">The published Windows build. Must carry upstream provenance; callers
    /// check <see cref="AgentPackage.UpstreamSha256"/> first and report
    /// <see cref="NoUpstreamProvenanceReason"/> when it is null.</param>
    /// <param name="apiBaseUrl">nginx's own address and port, resolved server-side — never a value
    /// a client supplied. See <c>AdminClientsController.ResolveAgentApiBaseUrl</c> for why the
    /// browser's own address is regularly the wrong answer.</param>
    /// <param name="enrollmentToken">The current <c>AGENT_ENROLLMENT_TOKEN</c>, or null. A blank
    /// one renders a script that installs and then fails enrollment with a legible reason, which is
    /// strictly better than one that silently sends an empty token.</param>
    public static string Build(AgentPackage package, string apiBaseUrl, string? enrollmentToken)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (package.UpstreamSha256 is null || package.UpstreamDownloadUrl is null)
        {
            throw new InvalidOperationException(NoUpstreamProvenanceReason);
        }

        return Template
            .Replace("__VERSION__", Literal(package.Version))
            .Replace("__ARCHIVE_NAME__", Literal(package.FileName))
            .Replace("__DOWNLOAD_URL__", Literal(package.UpstreamDownloadUrl))
            .Replace("__EXPECTED_SHA256__", Literal(package.UpstreamSha256))
            .Replace("__API_BASE_URL__", Literal(apiBaseUrl))
            .Replace("__ENROLLMENT_TOKEN__", Literal(enrollmentToken ?? string.Empty));
    }

    /// <summary>
    /// One PowerShell single-quoted string literal, including its quotes.
    /// </summary>
    /// <remarks>
    /// Single-quoted, so PowerShell performs no expansion inside it at all — a token or a URL
    /// containing <c>$</c> or a backtick is data rather than something to evaluate. Doubling an
    /// embedded apostrophe is then the only escape the form has, and the only one needed. This is
    /// the same reasoning <c>install.ps1</c> applies to the token it writes into <c>config.toml</c>:
    /// the value is a secret whose content nothing here controls.
    /// </remarks>
    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// The script text. Placeholders are <c>__NAME__</c> rather than interpolation holes because
    /// PowerShell's own <c>$()</c> and <c>{}</c> are all over the body, and a raw interpolated
    /// literal would need every brace in it doubled — which is exactly the kind of edit that
    /// silently changes a script nobody re-reads.
    /// </summary>
    private const string Template =
        """
        <#
        .SYNOPSIS
            Silently installs the Kintsugi patching agent on this Windows host.

        .DESCRIPTION
            Generated by the Kintsugi server for deployment through CrowdStrike Falcon (Real Time
            Response, or Falcon for IT) or any other tool that can run a PowerShell script as SYSTEM
            on a managed host. It needs nothing on the host beyond what Windows ships: curl.exe and
            tar.exe have both been present since Windows 10 1803 / Server 2019.

            What it does, in order: refuses to run unelevated; stops if the agent is already
            installed; downloads this exact release archive from GitHub; refuses to go on unless its
            SHA-256 matches the one written into this script; extracts it into a directory only
            SYSTEM and Administrators can write; points the packaged config.toml at this Kintsugi
            server; and hands off to the installer shipped inside the archive.

            THE PINNED CHECKSUM IS THE POINT. This script downloads from GitHub over a TLS
            connection that, on a managed estate, is very likely to be terminated and re-issued by
            an inspecting proxy holding a certificate this host trusts. TLS alone therefore proves
            nothing about who sent the bytes. The SHA-256 below travelled here inside this script,
            through the management channel that pushed it - so it is independent of that connection,
            and an archive that does not match it is not installed. Do not "fix" a mismatch by
            editing the hash to what was downloaded; re-render the script from the Kintsugi server's
            Clients screen instead, which is the only thing that knows the right value.

            THIS SCRIPT CONTAINS A LIVE ENROLLMENT TOKEN. Treat it as a credential. The token is
            rotatable on the server, so rotating it is the remedy if the script leaks.

            IT INSTALLS; IT DOES NOT UPGRADE. An installed agent updates itself from the Kintsugi
            server at every check-in, so the version pinned here only ever needs to be new enough to
            be worth handing to a brand-new host. A re-run against a host that already has the agent
            exits 0 and touches nothing, which is what makes this safe to push repeatedly.

            Exit codes: 0 installed, or already installed. Non-zero on any failure, with the reason
            on stdout and appended to the log file named at the end of the run.

        .PARAMETER Force
            Reinstall even when the agent is already present. Only for repairing a broken install -
            an ordinary redeploy should be a no-op.

        .EXAMPLE
            runscript -CloudFile="Install-KintsugiAgent" -CommandLine="-Force"
        #>
        [CmdletBinding()]
        param(
            [switch] $Force
        )

        $ErrorActionPreference = 'Stop'
        # Windows PowerShell 5.1 renders a progress bar for several of the cmdlets below, and in a
        # non-interactive host that rendering can dominate the runtime of the whole script.
        $ProgressPreference = 'SilentlyContinue'

        # ------------------------------------------------------------------------------------------------
        # What this build is. Every value here was rendered by the Kintsugi server; nothing below
        # discovers any of it at runtime, which is the point - see the checksum note in the synopsis.
        # ------------------------------------------------------------------------------------------------

        $Version         = __VERSION__
        $ArchiveName     = __ARCHIVE_NAME__
        $DownloadUrl     = __DOWNLOAD_URL__
        $ExpectedSha256  = __EXPECTED_SHA256__
        $ApiBaseUrl      = __API_BASE_URL__
        $EnrollmentToken = __ENROLLMENT_TOKEN__

        # Kept in step with clients\windows-agent\src\config.rs and packaging\install.ps1, which
        # resolve these same paths.
        $ServiceName = 'KintsugiAgent'
        $InstallDir  = Join-Path $env:ProgramFiles 'Kintsugi'
        $BinaryPath  = Join-Path $InstallDir 'kintsugi-agent.exe'
        $ConfigDir   = Join-Path $env:ProgramData 'Kintsugi\kintsugi-agent'
        $LogPath     = Join-Path $ConfigDir 'bootstrap.log'

        # ------------------------------------------------------------------------------------------------
        # Logging
        # ------------------------------------------------------------------------------------------------

        # To both a file and stdout: a management tool captures stdout for the operator watching the
        # push, and the file is what somebody reads on the host weeks later when one machine out of
        # four hundred turns out not to have the agent. The file sits beside the agent's own
        # service.log so there is one directory to look in.
        function Write-Log {
            param([string] $Message)

            $line = "[{0:yyyy-MM-dd HH:mm:ssZ}] {1}" -f (Get-Date).ToUniversalTime(), $Message
            Write-Output $line
            try {
                if (-not (Test-Path -LiteralPath $ConfigDir)) {
                    New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null
                }
                Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
            } catch {
                # Never let logging be the thing that fails the install.
            }
        }

        # ------------------------------------------------------------------------------------------------
        # Preconditions
        # ------------------------------------------------------------------------------------------------

        $WorkDir = $null
        try {
            Write-Log "Kintsugi agent bootstrap starting (target v$Version)."

            $identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
            if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                throw 'This script must run elevated. Under CrowdStrike RTR it runs as SYSTEM, which satisfies this; run it as Administrator if invoking by hand.'
            }

            # Already installed is the expected outcome of a redeploy, not a problem. The check is
            # "the service is registered and its binary is there" rather than a version comparison,
            # because the exe carries no version resource to compare against - and because the
            # installed agent updates itself from the Kintsugi server anyway, so reinstalling an
            # older pinned build over a newer running one would be a step backwards.
            $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            if ($service -and (Test-Path -LiteralPath $BinaryPath) -and -not $Force) {
                Write-Log "The $ServiceName service is already installed at $BinaryPath; nothing to do. Re-run with -Force to reinstall."
                exit 0
            }

            # Both have shipped in-box since Windows 10 1803 and Server 2019. Checked together and up
            # front so an unsupported host says so in one line, rather than downloading 8 MB and then
            # failing to unpack it. There is deliberately no fallback to Invoke-WebRequest: Windows
            # PowerShell 5.1's default SecurityProtocol excludes TLS 1.2, and GitHub answers such a
            # handshake with an opaque "could not create SSL/TLS secure channel" that reads as a
            # network fault rather than as a protocol one.
            foreach ($tool in @('curl.exe', 'tar.exe')) {
                if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
                    throw "$tool was not found on this host. It ships with Windows 10 1803 / Server 2019 and later; this script does not support anything older."
                }
            }

            # ------------------------------------------------------------------------------------------------
            # A working directory nothing unprivileged can write to
            # ------------------------------------------------------------------------------------------------

            # Running as SYSTEM puts $env:TEMP at C:\Windows\Temp, which any authenticated user may
            # write to. Verifying the archive and then extracting it somewhere writable would leave a
            # window in which a local process could swap the extracted binary between the check and
            # the install - so the checksum would pass and something else would run. The directory is
            # created without -Force (so a pre-created name is an error rather than a silent reuse)
            # and then stripped to SYSTEM and Administrators.
            $WorkDir = Join-Path $env:TEMP ('kintsugi-bootstrap-' + [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $WorkDir | Out-Null
            # SIDs, not names: S-1-5-18 is SYSTEM and S-1-5-32-544 is Administrators on every
            # Windows, localized or not - the same reason install.ps1 grants the queue directory by
            # SID.
            & icacls.exe $WorkDir /inheritance:r /grant '*S-1-5-18:(OI)(CI)F' /grant '*S-1-5-32-544:(OI)(CI)F' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not restrict permissions on $WorkDir (icacls exit code $LASTEXITCODE)." }

            # ------------------------------------------------------------------------------------------------
            # Download
            # ------------------------------------------------------------------------------------------------

            $ArchivePath = Join-Path $WorkDir $ArchiveName
            Write-Log "Downloading $ArchiveName from $DownloadUrl..."

            # --proto and --proto-redir pin the scheme on the first request and on every redirect
            # GitHub issues towards its asset storage, so a downgrade to http cannot be introduced by
            # a Location header. --tlsv1.2 sets the floor. -f makes an HTTP error status a non-zero
            # exit rather than a saved error page, which would otherwise reach the checksum test as
            # simply the wrong bytes.
            & curl.exe --silent --show-error --fail --location `
                --proto '=https' --proto-redir '=https' --tlsv1.2 `
                --retry 3 --retry-delay 5 --connect-timeout 30 --max-time 900 `
                --output $ArchivePath $DownloadUrl
            if ($LASTEXITCODE -ne 0) { throw "Downloading $DownloadUrl failed (curl exit code $LASTEXITCODE)." }
            if (-not (Test-Path -LiteralPath $ArchivePath)) { throw "curl reported success but $ArchivePath was not written." }

            # ------------------------------------------------------------------------------------------------
            # Verify, before anything out of the archive is read or run
            # ------------------------------------------------------------------------------------------------

            $actualSha256 = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash
            if ($actualSha256 -ine $ExpectedSha256) {
                # Deliberately fatal and deliberately loud. The two innocent causes are a truncated
                # download and a re-cut release; the one that matters is bytes that are not the ones
                # this server hashed. None of the three is a reason to install what arrived.
                throw ("The downloaded archive does not match the checksum pinned in this script, so it will not be installed. " +
                       "Expected $ExpectedSha256 but got $($actualSha256.ToLowerInvariant()). " +
                       "Re-render this script from the Kintsugi server's Clients screen rather than editing the hash.")
            }
            Write-Log "Checksum verified: $ExpectedSha256"

            # ------------------------------------------------------------------------------------------------
            # Extract and point at this server
            # ------------------------------------------------------------------------------------------------

            # The archive's top-level entry names are load-bearing across this repo - self_update.rs
            # extracts "kintsugi-agent.exe" by name - so this unpacks flat and expects them where
            # publish-release.ps1 put them.
            & tar.exe -xzf $ArchivePath -C $WorkDir
            if ($LASTEXITCODE -ne 0) { throw "Extracting $ArchiveName failed (tar exit code $LASTEXITCODE)." }

            $installer = Join-Path $WorkDir 'install.ps1'
            $configPath = Join-Path $WorkDir 'config.toml'
            foreach ($required in @($installer, $configPath, (Join-Path $WorkDir 'kintsugi-agent.exe'))) {
                if (-not (Test-Path -LiteralPath $required)) { throw "The archive did not contain $(Split-Path -Leaf $required)." }
            }

            # The GitHub archive ships the placeholder kintsugi.example.com, because a real address
            # must never be committed to a public repository - a package downloaded from the Kintsugi
            # server has this rewritten already (AgentPackageArchiveRewriter), and one taken straight
            # from GitHub does not. install.ps1 copies this file verbatim, so this is where the
            # address gets set.
            #
            # Rewritten by dropping the line and appending, not by substitution, and written with a
            # BOM-less UTF-8 encoder - both for the reasons install.ps1 states where it does the same
            # to the enrollment token. A BOM is not cosmetic here: config.rs parses this with the
            # `toml` crate, which rejects a leading U+FEFF, and Config::load_from then falls back to
            # built-in defaults, so the symptom would be an agent quietly ignoring the address just
            # set for it.
            Write-Log "Pointing the packaged config.toml at $ApiBaseUrl..."
            $escapedUrl = $ApiBaseUrl.Replace('\', '\\').Replace('"', '\"')
            $kept = Get-Content -LiteralPath $configPath | Where-Object { $_ -notmatch '^\s*api_base_url' }
            $lines = @($kept) + ('api_base_url = "' + $escapedUrl + '"')
            [System.IO.File]::WriteAllLines($configPath, $lines, (New-Object System.Text.UTF8Encoding($false)))

            # ------------------------------------------------------------------------------------------------
            # Hand off to the archive's own installer
            # ------------------------------------------------------------------------------------------------

            # Not reimplemented here on purpose: install.ps1 pins `obj= LocalSystem` on the service
            # (which the identity directory's ACL depends on, and which SE_TCB_NAME - and so remote
            # control's session helper - depends on as well), grants the queue directory by SID rather
            # than by the localized name "Users", and writes config.toml BOM-less. A second copy of
            # that logic would drift, and each of those three fails quietly when it does.
            #
            # Run in a child powershell.exe with -ExecutionPolicy Bypass so a machine policy that
            # blocks scripts does not block this one, and -NonInteractive so nothing can sit waiting
            # for input on a host with nobody at it.
            Write-Log "Running the packaged installer for v$Version..."

            # -EnrollmentToken is appended only when there is one. Windows PowerShell drops an empty
            # string when building a native command line, so passing one would leave install.ps1
            # looking at a bare "-EnrollmentToken" with nothing after it and failing on a missing
            # argument - turning "this server has no token configured yet", which install.ps1 handles
            # with a legible warning, into a failed install.
            $installerArgs = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $installer)
            if ($EnrollmentToken) { $installerArgs += @('-EnrollmentToken', $EnrollmentToken) }

            & powershell.exe @installerArgs
            $installerExit = $LASTEXITCODE
            if ($installerExit -ne 0) {
                throw "The packaged installer failed (exit code $installerExit). See $ConfigDir\service.log."
            }

            Write-Log "Kintsugi agent v$Version installed. Service log: $ConfigDir\service.log"
            Write-Log "Bootstrap log: $LogPath"
            exit 0
        } catch {
            Write-Log "FAILED: $($_.Exception.Message)"
            Write-Log "Bootstrap log: $LogPath"
            # An explicit non-zero exit, rather than letting the exception set one: a management tool
            # decides whether a host succeeded from this number, and a `throw` escaping a script run
            # with -Command rather than -File does not reliably produce one.
            exit 1
        } finally {
            # The archive carries no secret, but the config.toml written into it carries the api
            # address and the installer copied it where it belongs; leaving a tree behind in a
            # world-readable temp directory buys nothing.
            if ($WorkDir -and (Test-Path -LiteralPath $WorkDir)) {
                Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        """;
}
