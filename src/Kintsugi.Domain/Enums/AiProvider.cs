namespace Kintsugi.Domain.Enums;

/// <summary>
/// Which AI agent researches upgrade paths and writes the scripts agents run.
///
/// Members are appended, never inserted. The value is persisted as its *name* (see
/// <c>AiAgentSettingsConfiguration</c>), so the database is indifferent to order — but the admin
/// UI is not: <c>AiProvider</c> carries no JSON converter, so System.Text.Json writes it as an
/// ordinal and <c>web/lib/domain/entities/enums.dart</c> mirrors this declaration order by hand.
/// A member inserted anywhere but the end silently re-maps every value the client reads.
/// </summary>
public enum AiProvider
{
    Anthropic,
    OpenAI,
    Ollama,
    GooseCli,

    /// <summary>
    /// Claude, driven through the Claude Agent SDK rather than through the Anthropic API — the
    /// <c>claude</c> CLI run as a subprocess, which is the SDK's documented interface for a host
    /// language it has no library for (there is no C# Agent SDK). See
    /// <c>ClaudeAgentSdkClient</c>. It differs from <see cref="Anthropic"/> in what it bills:
    /// <see cref="Anthropic"/> spends metered API credits, while this authenticates with this
    /// host's own Claude Code login and so spends that subscription's included usage.
    /// </summary>
    ClaudeAgentSdk
}
