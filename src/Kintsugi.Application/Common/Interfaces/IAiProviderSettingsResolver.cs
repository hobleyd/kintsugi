using Kintsugi.Application.AiSettings;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>Loads everything a call to the AI needs, or null when AI is not configured — the one
/// place that answers "is the AI configured?". See <c>AiProviderSettingsResolver</c>.</summary>
public interface IAiProviderSettingsResolver
{
    /// <summary>Everything an AI call needs, or null when AI is disabled or not configured.</summary>
    Task<AiProviderSettings?> ResolveAsync(CancellationToken cancellationToken);
}
