namespace Kintsugi.Application.Vanta;

/// <summary>
/// What one sync run did. Carries no enum deliberately: several enums in this system cross the wire
/// as ordinals and several as names (see CLAUDE.md), and a status that only ever gets rendered as a
/// sentence has nothing to gain from joining that list.
/// </summary>
/// <param name="Attempted">False when nothing was sent at all — the integration is switched off,
/// incompletely configured, or had nothing safe to send. <paramref name="Message"/> says which.</param>
/// <param name="Succeeded">True only when both resource collections reached Vanta.</param>
/// <param name="ComponentCount">Components sent (hosts). Zero whenever
/// <paramref name="Attempted"/> is false.</param>
/// <param name="PackageCount">Package vulnerabilities sent. Zero is a legitimate, meaningful
/// result: it is how a fully patched fleet clears everything Vanta was previously holding.</param>
/// <param name="Message">Why it was skipped, or why it failed, or a plain summary of what was sent.
/// Shown as-is on the settings screen.</param>
public record VantaSyncResultDto(
    bool Attempted,
    bool Succeeded,
    int ComponentCount,
    int PackageCount,
    string Message);
