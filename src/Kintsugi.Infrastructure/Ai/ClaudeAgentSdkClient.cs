using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.Ai;

/// <summary>
/// Drives Claude through the Claude Agent SDK — which, for a host language the SDK ships no
/// library for, means running the <c>claude</c> CLI as a subprocess with <c>-p</c> and
/// <c>--output-format json</c>. That is the SDK's own documented interface for exactly this case;
/// there is no C# Agent SDK to reference instead.
///
/// Unlike <see cref="GooseCliClient"/>, which reaches a Goose agent over the network because this
/// system's container has no Goose of its own, the binary here *is* in the image: the runtime
/// stage installs <c>claude-code</c> from Anthropic's apt repository the same way it installs
/// PowerShell from Microsoft's (see <c>src/Kintsugi.WebApi/Dockerfile</c>). Nothing has to be
/// reachable over the network for this provider to work, and no second service has to be run.
///
/// **The point of this provider is which meter the run lands on.** <c>AiProvider.Anthropic</c>
/// calls <c>api.anthropic.com</c> with an API key and spends metered credits. This one
/// authenticates with the one-year OAuth token <c>claude setup-token</c> prints, which bills the
/// Claude subscription that minted it. Everything below that looks like belt-and-braces —
/// scrubbing the environment, refusing <c>--bare</c> — exists to keep that true, because every way
/// of getting it wrong produces a *correct answer on the wrong meter*, which nothing surfaces.
/// </summary>
public class ClaudeAgentSdkClient : IClaudeAgentSdkClient
{
    // A single research turn asks the model to research an application and author a complete,
    // validated two-mode script, with web search along the way — the same merged prompt Goose is
    // given, and the same reason for a generous ceiling: this runs as a background job (see
    // UpgradePathRefreshCoordinator), so nothing is waiting on it.
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(1500);

    // The availability check makes a real (one-turn, near-empty) model request, so it is bounded
    // by round-trip latency rather than by process startup. Ten seconds — what the Goose status
    // check allows for a handshake — would report a working configuration as broken. A *rejected*
    // token does not need the whole budget: a 401 is reported as the result within a few seconds
    // ("Failed to authenticate. API Error: 401 OAuth access token is invalid."), because the CLI
    // stops retrying an authentication failure rather than working through its ten attempts.
    private static readonly TimeSpan StatusCheckTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan VersionCheckTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Environment variables that would quietly outrank <c>CLAUDE_CODE_OAUTH_TOKEN</c> if the
    /// container happened to carry them. Claude Code's credential precedence puts a cloud-provider
    /// selection, then <c>ANTHROPIC_AUTH_TOKEN</c>, then <c>ANTHROPIC_API_KEY</c>, then an
    /// Anthropic profile *above* the OAuth token — so a deployment that also sets
    /// <c>ANTHROPIC_API_KEY</c> for the <c>Anthropic</c> provider (a perfectly ordinary thing for
    /// a `.env` to do) would have this provider silently bill the API instead of the subscription.
    /// The output would be identical; only the bill would differ. They are therefore *removed*
    /// from the child's environment rather than merely overridden.
    /// </summary>
    private static readonly string[] OutrankingCredentialVariables =
    {
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_PROFILE",
        "ANTHROPIC_FEDERATION_RULE_ID",
        "ANTHROPIC_ORGANIZATION_ID",
        "ANTHROPIC_WORKSPACE_ID",
        "CLAUDE_CODE_USE_BEDROCK",
        "CLAUDE_CODE_USE_VERTEX",
        "CLAUDE_CODE_USE_FOUNDRY"
    };

    private const string DefaultExecutable = "claude";
    private const string DefaultConfigDirectory = "/data/claude-code";
    private const string DefaultWorkingDirectory = "/data/claude-code/workspace";

    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IConfiguration _configuration;
    private readonly ILogger<ClaudeAgentSdkClient> _logger;

    public ClaudeAgentSdkClient(IConfiguration configuration, ILogger<ClaudeAgentSdkClient> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ClaudeAgentSdkStatus> CheckAvailabilityAsync(string? oauthToken, CancellationToken cancellationToken)
    {
        string version;

        try
        {
            var versionResult = await RunProcessAsync(
                new[] { "--version" }, oauthToken, VersionCheckTimeout, cancellationToken);

            if (versionResult.ExitCode != 0)
            {
                return new ClaudeAgentSdkStatus(false, null, Describe(versionResult));
            }

            version = versionResult.Stdout.Trim();
        }
        catch (Win32Exception)
        {
            return new ClaudeAgentSdkStatus(false, null,
                $"The '{Executable}' command is not installed on this server. It is installed by the API image; a deployment running an older image will not have it.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ClaudeAgentSdkStatus(false, null, "Timed out running `claude --version`.");
        }

        if (string.IsNullOrWhiteSpace(oauthToken))
        {
            return new ClaudeAgentSdkStatus(false, version,
                "No OAuth token is saved yet. Run `claude setup-token` on a machine signed in to the Claude subscription this server should use, paste the token above and save.");
        }

        // `claude --version` proves only that the binary is present. The token is the half that
        // actually expires (a year), belongs to one subscription, and needs a plan that includes
        // Claude Code — so the check makes one real, minimal model request. Reporting "available"
        // off the version alone would call an expired token healthy and leave every research run
        // failing with nothing on this screen to say why.
        try
        {
            var probe = await RunProcessAsync(
                new[]
                {
                    "-p", "Reply with the single word: ok",
                    "--output-format", "json",
                    "--max-turns", "1",
                    "--permission-mode", "dontAsk",
                    "--no-session-persistence"
                },
                oauthToken,
                StatusCheckTimeout,
                cancellationToken);

            var (_, error) = ReadResult(probe);

            return error is null
                ? new ClaudeAgentSdkStatus(true, version, null)
                : new ClaudeAgentSdkStatus(false, version, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ClaudeAgentSdkStatus(false, version,
                $"Timed out waiting for a reply after {StatusCheckTimeout.TotalSeconds:0} seconds. A rejected token is normally reported within seconds, so this usually means the request itself could not be made — check that this server can reach api.anthropic.com.");
        }
    }

    public async Task<string> RunAsync(string prompt, string? model, string? oauthToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(oauthToken))
        {
            throw new ExternalServiceException(
                "No Claude Code OAuth token is configured. Run `claude setup-token` and save the token it prints in Settings > AI Agent.");
        }

        var arguments = new List<string>
        {
            "-p", prompt,
            "--output-format", "json",
            // -p starts in the Manual permission mode on every plan, which denies every tool call
            // it was not told about. Left at that default the agent researches nothing and answers
            // from memory alone — a fail-open degradation with no error to notice. `dontAsk`
            // denies anything outside the allow list rather than blocking on a prompt nobody is
            // there to answer, and the allow list is exactly the two read-only research tools.
            // Deliberately *not* --dangerously-skip-permissions: this container holds the fleet CA
            // and the signing key, and the whole approval model rests on generated scripts being
            // inert text until a human signs them.
            "--permission-mode", "dontAsk",
            "--allowedTools", "WebSearch,WebFetch",
            // Nothing resumes these sessions, and the transcript would otherwise accumulate in the
            // config volume for the life of the deployment.
            "--no-session-persistence"
        };

        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.Add("--model");
            arguments.Add(model.Trim());
        }

        ProcessResult result;

        try
        {
            result = await RunProcessAsync(arguments, oauthToken, RunTimeout, cancellationToken);
        }
        catch (Win32Exception ex)
        {
            throw new ExternalServiceException(
                $"The '{Executable}' command is not installed on this server, so the Claude Agent SDK cannot be used.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Claude Agent SDK run timed out after {Timeout}", RunTimeout);
            throw new ExternalServiceException(
                $"Timed out waiting for the Claude Agent SDK to respond after {RunTimeout.TotalMinutes:0} minutes.", ex);
        }

        var (text, error) = ReadResult(result);

        if (error is not null)
        {
            throw new ExternalServiceException(error);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ExternalServiceException("The Claude Agent SDK returned an empty response.");
        }

        return text;
    }

    /// <summary>Reads the <c>--output-format json</c> envelope. A run that fails *inside* the
    /// session — an expired token, a plan without Claude Code, a rate limit — is reported as the
    /// result on stdout rather than as a non-zero exit, so the exit code alone is not enough to
    /// tell success from failure.</summary>
    private static (string? Text, string? Error) ReadResult(ProcessResult result)
    {
        ClaudeCodeResult? envelope = null;

        if (!string.IsNullOrWhiteSpace(result.Stdout))
        {
            try
            {
                envelope = JsonSerializer.Deserialize<ClaudeCodeResult>(result.Stdout, ResultJsonOptions);
            }
            catch (JsonException)
            {
                // Not the JSON envelope — fall through to the exit-code path below, which reports
                // whatever was actually printed.
            }
        }

        if (envelope is null)
        {
            return (null, Describe(result));
        }

        if (envelope.IsError || result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(envelope.Result) ? Describe(result) : envelope.Result.Trim();
            return (null, Explain(detail));
        }

        return (envelope.Result, null);
    }

    /// <summary>Appends what to actually do about an authentication failure. The CLI's own wording
    /// assumes a terminal — "Not logged in · Please run /login" — and `/login` is an interactive
    /// browser flow that does not exist here; the equivalent for this deployment is minting a token
    /// elsewhere and saving it on the settings page.</summary>
    private static string Explain(string detail)
    {
        var looksLikeAuth =
            detail.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("authenticate", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("OAuth", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("401", StringComparison.Ordinal);

        return looksLikeAuth
            ? $"{detail} — run `claude setup-token` on a machine signed in to the Claude subscription this server should use, and save the token it prints in Settings > AI Agent."
            : detail;
    }

    private static string Describe(ProcessResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        detail = detail.Trim();

        return string.IsNullOrWhiteSpace(detail)
            ? $"`claude` exited with code {result.ExitCode} and printed nothing."
            : detail;
    }

    private async Task<ProcessResult> RunProcessAsync(
        IReadOnlyList<string> arguments,
        string? oauthToken,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Executable,
            WorkingDirectory = EnsureWorkingDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // ProcessStartInfo.Environment starts as a copy of this process's, so every removal below
        // is load-bearing rather than defensive. See OutrankingCredentialVariables.
        foreach (var variable in OutrankingCredentialVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        if (!string.IsNullOrWhiteSpace(oauthToken))
        {
            startInfo.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = oauthToken;
        }

        // `--bare` would be the obvious flag for a scripted call — it skips discovery of hooks,
        // skills, MCP servers and CLAUDE.md, and starts faster. It must not be used here: bare
        // mode never reads OAuth credentials at all and requires ANTHROPIC_API_KEY, which is
        // precisely the meter this provider exists to avoid. The working directory below is an
        // empty one for the same reason bare mode is attractive — with discovery on, whatever sits
        // in the process's own directory would be read as agent configuration.
        startInfo.Environment["CLAUDE_CONFIG_DIR"] = ConfigDirectory;

        // The image pins a version through apt; letting the binary replace itself under a running
        // fleet would make "which version answered" unanswerable after the fact.
        startInfo.Environment["DISABLE_AUTOUPDATER"] = "1";
        startInfo.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort — it may already have exited.
        }
    }

    private string Executable => Resolve("CLAUDE_CODE_EXECUTABLE", DefaultExecutable);

    private string ConfigDirectory => Resolve("CLAUDE_CONFIG_DIR", DefaultConfigDirectory);

    private string WorkingDirectory => Resolve("CLAUDE_CODE_WORKING_DIRECTORY", DefaultWorkingDirectory);

    /// <summary>The directory the CLI is run from — deliberately an empty one it owns, because
    /// without <c>--bare</c> (see <see cref="RunProcessAsync"/>) it reads <c>.claude/</c>,
    /// <c>.mcp.json</c> and <c>CLAUDE.md</c> out of wherever it starts, and the process's own
    /// directory is the published application. Falls back to a temp directory rather than failing
    /// the run if the configured one cannot be created.</summary>
    private string EnsureWorkingDirectory()
    {
        var directory = WorkingDirectory;

        try
        {
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not create the Claude Agent SDK working directory {Directory}", directory);

            var fallback = Path.Combine(Path.GetTempPath(), "claude-agent-sdk");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private string Resolve(string key, string fallback)
    {
        var value = _configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private class ClaudeCodeResult
    {
        [JsonPropertyName("result")]
        public string? Result { get; set; }

        [JsonPropertyName("is_error")]
        public bool IsError { get; set; }

        [JsonPropertyName("subtype")]
        public string? Subtype { get; set; }
    }
}
