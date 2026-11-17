using System.Text.Json.Nodes;

namespace Kintsugi.Infrastructure.Ai.Engine;

public enum AiChatRole
{
    User,
    Assistant,
    Tool,
}

/// <summary>A tool call the model asked for, normalized across protocols.</summary>
/// <param name="Id">The provider's id for the call, echoed back with its result. Gemini issues
/// none, so its adapter makes one up from the name and position.</param>
public record AiToolCall(string Id, string Name, JsonObject Arguments);

/// <summary>
/// One turn of a conversation, in a shape no protocol owns.
/// </summary>
/// <param name="ProviderContent">An assistant turn exactly as the provider returned it, replayed
/// verbatim on the next request instead of being rebuilt. That is a correctness requirement, not
/// an optimization: Anthropic insists on its own <c>tool_use</c> blocks coming back, and Gemini
/// rejects a function call returned without the <c>thoughtSignature</c> it was issued with. Only
/// ever replayed to the adapter that produced it — a loop never switches protocol mid-run.</param>
public record AiChatMessage(
    AiChatRole Role,
    string? Text,
    IReadOnlyList<AiToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? ToolName = null,
    JsonNode? ProviderContent = null)
{
    public static AiChatMessage User(string text) => new(AiChatRole.User, text);

    public static AiChatMessage ToolResult(AiToolCall call, string result) =>
        new(AiChatRole.Tool, result, ToolCallId: call.Id, ToolName: call.Name);
}

/// <summary>A function tool offered to the model.</summary>
/// <param name="Parameters">A JSON Schema object.</param>
public record AiToolDefinition(string Name, string Description, JsonObject Parameters);

/// <param name="UseHostedWebSearch">Ask for the provider's own web search tool. Ignored by a
/// protocol that has none.</param>
/// <param name="MaxOutputTokens">Null leaves it to the server, except where the protocol requires
/// one (Anthropic), when the adapter supplies a default.</param>
public record AiChatRequest(
    string Model,
    IReadOnlyList<AiChatMessage> Messages,
    IReadOnlyList<AiToolDefinition> Tools,
    bool UseHostedWebSearch,
    int? MaxOutputTokens);

/// <summary>Tokens one call used, as the provider reported them.</summary>
public record AiTokenUsage(long InputTokens, long OutputTokens)
{
    /// <summary>Reads a token count a provider reported, tolerating it being absent or not a number.</summary>
    internal static long Count(System.Text.Json.Nodes.JsonNode? node)
    {
        try
        {
            return node is null ? 0 : node.GetValue<long>();
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>Null when the provider reported nothing, so "no usage" is not recorded as "zero".</summary>
    internal static AiTokenUsage? Of(long input, long output) => input == 0 && output == 0 ? null : new AiTokenUsage(input, output);
}

/// <param name="Text">The visible text of the reply — reasoning and hosted-tool chatter excluded.</param>
/// <param name="Assistant">The reply as a message to append to the conversation.</param>
/// <param name="Usage">What the provider said the call used; null when it said nothing.</param>
public record AiChatResult(string Text, IReadOnlyList<AiToolCall> ToolCalls, AiChatMessage Assistant, AiTokenUsage? Usage = null);
