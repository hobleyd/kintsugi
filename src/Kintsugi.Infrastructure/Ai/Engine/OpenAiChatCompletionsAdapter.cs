using System.Text.Json.Nodes;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Ai.Engine;

/// <summary>
/// <c>POST {base}/chat/completions</c> — the shape nearly every third-party and self-hosted server
/// copies: LiteLLM, vLLM, LM Studio, OpenRouter, Groq, Mistral, Azure's <c>/openai/v1</c>, and
/// Vertex AI's OpenAI-compatible endpoint (under Google Cloud authentication, with that endpoint
/// as the base URL). No hosted web search: research on this protocol runs on Kintsugi's own tools.
/// </summary>
/// <remarks>
/// No <c>max_tokens</c> is sent. OpenAI's reasoning models reject it in favour of
/// <c>max_completion_tokens</c>, which half the compatible servers do not know — and a script is
/// well inside every server's default.
/// </remarks>
public class OpenAiChatCompletionsAdapter : IAiProtocolAdapter
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";

    private readonly AiHttp _http;

    public OpenAiChatCompletionsAdapter(AiHttp http)
    {
        _http = http;
    }

    public AiWireProtocol Protocol => AiWireProtocol.OpenAiChatCompletions;

    public async Task<AiChatResult> SendAsync(AiConnectionSettings connection, AiChatRequest request, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray(request.Messages.Select(ToWire).ToArray()),
        };

        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.Parameters.DeepClone(),
                },
            }).ToArray());
        }

        var url = (connection.BaseUrl ?? DefaultBaseUrl).TrimEnd('/') + "/chat/completions";
        var response = await _http.PostJsonAsync(connection, url, body, cancellationToken);

        var message = response["choices"]?[0]?["message"] as JsonObject
            ?? throw new ExternalServiceException($"The AI provider ({connection.Name}) returned no message.");

        var toolCalls = new List<AiToolCall>();
        if (message["tool_calls"] is JsonArray calls)
        {
            foreach (var call in calls.OfType<JsonObject>())
            {
                var function = call["function"] as JsonObject;
                toolCalls.Add(new AiToolCall(
                    call["id"]?.GetValue<string>() ?? $"call_{toolCalls.Count}",
                    function?["name"]?.GetValue<string>() ?? string.Empty,
                    ParseArguments(function?["arguments"])));
            }
        }

        var text = ReadContent(message["content"]);
        return new AiChatResult(text, toolCalls, new AiChatMessage(AiChatRole.Assistant, text, toolCalls, ProviderContent: message.DeepClone()));
    }

    private static JsonNode ToWire(AiChatMessage message) => message.Role switch
    {
        AiChatRole.User => new JsonObject { ["role"] = "user", ["content"] = message.Text ?? string.Empty },
        AiChatRole.Tool => new JsonObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = message.ToolCallId,
            ["content"] = message.Text ?? string.Empty,
        },
        // The provider's own message object, so whatever extra fields it carried survive.
        _ => message.ProviderContent?.DeepClone() ?? new JsonObject { ["role"] = "assistant", ["content"] = message.Text ?? string.Empty },
    };

    /// <summary>Arguments arrive as a JSON-encoded string here, and as an object from a few
    /// compatible servers that read the spec loosely.</summary>
    internal static JsonObject ParseArguments(JsonNode? arguments)
    {
        try
        {
            return arguments switch
            {
                JsonObject obj => (JsonObject)obj.DeepClone(),
                JsonValue value when value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) =>
                    JsonNode.Parse(s) as JsonObject ?? new JsonObject(),
                _ => new JsonObject(),
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject();
        }
    }

    /// <summary>A string, or — from some servers — an array of typed parts.</summary>
    internal static string ReadContent(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        JsonArray parts => string.Join("\n", parts.OfType<JsonObject>()
            .Where(p => p["text"] is not null)
            .Select(p => p["text"]!.GetValue<string>())),
        _ => string.Empty,
    };
}
