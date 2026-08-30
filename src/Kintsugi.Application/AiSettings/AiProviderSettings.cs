using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.AiSettings;

/// <summary>The connection details a call to the configured AI provider actually needs — a plain
/// value, as opposed to the <c>AiAgentSettings</c> domain entity, so callers outside the handler
/// that loaded it (e.g. a background research task) don't need to carry a tracked entity around.</summary>
public record AiProviderSettings(AiProvider Provider, string? ApiKey, string? BaseUrl, string? Model);
