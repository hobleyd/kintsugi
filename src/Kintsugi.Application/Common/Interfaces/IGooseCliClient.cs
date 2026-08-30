namespace Kintsugi.Application.Common.Interfaces;

public interface IGooseCliClient
{
    /// <summary>Connects to the `goose serve` instance at <paramref name="endpoint"/> (its base URL;
    /// blank resolves to Goose's own default local address) and performs the ACP handshake, to
    /// power a status check in Settings before the agent is relied on for real requests.</summary>
    Task<GooseCliStatus> CheckAvailabilityAsync(string? endpoint, CancellationToken cancellationToken);

    /// <summary>Runs <paramref name="prompt"/> as a single headless ACP session against the `goose
    /// serve` instance at <paramref name="endpoint"/> and returns the agent's collected text reply.
    /// <paramref name="model"/>, when set, is matched (best-effort) against the "model" session
    /// config option the agent advertises; no match means the agent's own current model is used.</summary>
    Task<string> RunAsync(string prompt, string? model, string? endpoint, CancellationToken cancellationToken);
}

public record GooseCliStatus(bool IsAvailable, string? Version, string? Error);
