namespace Kintsugi.Application.Common.Interfaces;

public interface IClaudeAgentSdkClient
{
    /// <summary>Reports whether the Claude Agent SDK can actually be used from this server, to
    /// power a status check in Settings before the agent is relied on for real requests. Both
    /// halves matter and both are checked: that the <c>claude</c> binary is installed, and that
    /// <paramref name="oauthToken"/> still authenticates — a token is valid for a year, belongs to
    /// one subscription, and is the only thing standing between "configured" and every research
    /// run failing.</summary>
    Task<ClaudeAgentSdkStatus> CheckAvailabilityAsync(string? oauthToken, CancellationToken cancellationToken);

    /// <summary>Runs <paramref name="prompt"/> as a single headless Agent SDK session and returns
    /// the agent's collected text reply. <paramref name="model"/>, when set, is passed straight to
    /// <c>--model</c> (an alias such as <c>opus</c> or a full model id); blank uses whichever model
    /// the CLI defaults to. <paramref name="oauthToken"/> is the token <c>claude setup-token</c>
    /// prints, and is what makes the run bill a Claude subscription rather than API credits.</summary>
    Task<string> RunAsync(string prompt, string? model, string? oauthToken, CancellationToken cancellationToken);
}

/// <summary>Mirrors <c>GooseCliStatus</c> deliberately rather than sharing a type with it: the two
/// probes answer the same question about different things, and one screen shows whichever the
/// selected provider needs. <c>Version</c> is the installed <c>claude</c> version.</summary>
public record ClaudeAgentSdkStatus(bool IsAvailable, string? Version, string? Error);
