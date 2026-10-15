using System.Text.Json.Nodes;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Ai.Engine;

/// <summary>
/// Anthropic's Messages API: <c>api.anthropic.com</c> by default, any Anthropic-compatible host via
/// the base URL, or Claude on Vertex AI under Google Cloud authentication —
/// <c>…/publishers/anthropic/models/{model}:rawPredict</c>, where the model moves from the body
/// into the URL and <c>anthropic_version</c> moves from a header into the body.
/// </summary>
public class AnthropicAdapter : IAiProtocolAdapter
{
    public const string DefaultBaseUrl = "https://api.anthropic.com/v1";

    /// <summary>The Messages API requires a ceiling; this is the one research always used.</summary>
    public const int DefaultMaxTokens = 8192;

    private readonly AiHttp _http;

    public AnthropicAdapter(AiHttp http)
    {
        _http = http;
    }

    public AiWireProtocol Protocol => AiWireProtocol.Anthropic;

    public async Task<AiChatResult> SendAsync(AiConnectionSettings connection, AiChatRequest request, CancellationToken cancellationToken)
    {
        var onVertex = connection.AuthMode == AiAuthMode.GoogleCloud;

        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case AiChatRole.User:
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = message.Text ?? string.Empty });
                    break;
                case AiChatRole.Tool:
                    // Consecutive results belong in one user turn; the API rejects two user turns
                    // in a row.
                    var result = new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = message.ToolCallId,
                        ["content"] = message.Text ?? string.Empty,
                    };
                    if (messages.Count > 0 && messages[^1] is JsonObject last
                        && last["role"]?.GetValue<string>() == "user" && last["content"] is JsonArray blocks)
                    {
                        blocks.Add(result);
                    }
                    else
                    {
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(result) });
                    }

                    break;
                default:
                    messages.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = message.ProviderContent?.DeepClone() ?? JsonValue.Create(message.Text ?? string.Empty),
                    });
                    break;
            }
        }

        var tools = new JsonArray();
        if (request.UseHostedWebSearch)
        {
            tools.Add(new JsonObject { ["type"] = "web_search_20250305", ["name"] = "web_search", ["max_uses"] = 3 });
        }

        foreach (var tool in request.Tools)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = tool.Parameters.DeepClone(),
            });
        }

        var body = new JsonObject
        {
            ["max_tokens"] = request.MaxOutputTokens ?? DefaultMaxTokens,
            ["messages"] = messages,
        };
        if (tools.Count > 0)
        {
            body["tools"] = tools;
        }

        string url;
        if (onVertex)
        {
            body["anthropic_version"] = "vertex-2023-10-16";
            url = $"{AiHttp.VertexBaseUrl(connection.GoogleCloudProject!, connection.GoogleCloudLocation!)}/publishers/anthropic/models/{Uri.EscapeDataString(request.Model)}:rawPredict";
        }
        else
        {
            body["model"] = request.Model;
            url = (connection.BaseUrl ?? DefaultBaseUrl).TrimEnd('/') + "/messages";
        }

        var response = await _http.PostJsonAsync(connection, url, body, cancellationToken, extraHeaders: onVertex ? null : AnthropicVersionHeader);

        var content = response["content"] as JsonArray
            ?? throw new ExternalServiceException($"The AI provider ({connection.Name}) returned no content.");

        var text = new List<string>();
        var toolCalls = new List<AiToolCall>();
        foreach (var block in content.OfType<JsonObject>())
        {
            switch (block["type"]?.GetValue<string>())
            {
                case "text" when block["text"]?.GetValue<string>() is { Length: > 0 } t:
                    text.Add(t);
                    break;
                case "tool_use":
                    toolCalls.Add(new AiToolCall(
                        block["id"]?.GetValue<string>() ?? $"toolu_{toolCalls.Count}",
                        block["name"]?.GetValue<string>() ?? string.Empty,
                        block["input"] as JsonObject is { } input ? (JsonObject)input.DeepClone() : new JsonObject()));
                    break;
            }
        }

        var joined = string.Join("\n", text);
        return new AiChatResult(joined, toolCalls, new AiChatMessage(AiChatRole.Assistant, joined, toolCalls, ProviderContent: content.DeepClone()));
    }

    private static readonly IReadOnlyDictionary<string, string> AnthropicVersionHeader =
        new Dictionary<string, string> { ["anthropic-version"] = "2023-06-01" };
}
