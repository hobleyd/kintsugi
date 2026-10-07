using System.Text.Json.Nodes;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Ai.Engine;

/// <summary>Ollama's own <c>/api/chat</c>, non-streaming. No hosted search: research runs on
/// Kintsugi's tools, as it always did through Ollama's web search API.</summary>
public class OllamaAdapter : IAiProtocolAdapter
{
    private readonly AiHttp _http;

    public OllamaAdapter(AiHttp http)
    {
        _http = http;
    }

    public AiWireProtocol Protocol => AiWireProtocol.Ollama;

    public async Task<AiChatResult> SendAsync(AiConnectionSettings connection, AiChatRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connection.BaseUrl))
        {
            throw new ExternalServiceException("No Ollama endpoint URL is configured.");
        }

        if (!Uri.TryCreate(connection.BaseUrl.TrimEnd('/') + "/api/chat", UriKind.Absolute, out var uri))
        {
            throw new ExternalServiceException("The configured Ollama endpoint URL is not valid.");
        }

        var messages = new JsonArray(request.Messages.Select(m => (JsonNode)(m.Role switch
        {
            AiChatRole.User => new JsonObject { ["role"] = "user", ["content"] = m.Text ?? string.Empty },
            AiChatRole.Tool => new JsonObject { ["role"] = "tool", ["content"] = m.Text ?? string.Empty, ["tool_name"] = m.ToolName },
            _ => m.ProviderContent?.DeepClone() as JsonObject ?? new JsonObject { ["role"] = "assistant", ["content"] = m.Text ?? string.Empty },
        })).ToArray());

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = false,
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

        var response = await _http.PostJsonAsync(connection, uri.ToString(), body, cancellationToken);
        var message = response["message"] as JsonObject
            ?? throw new ExternalServiceException($"The AI provider ({connection.Name}) returned no message.");

        var toolCalls = new List<AiToolCall>();
        foreach (var call in (message["tool_calls"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var function = call["function"] as JsonObject;
            toolCalls.Add(new AiToolCall(
                $"call_{toolCalls.Count}",
                function?["name"]?.GetValue<string>() ?? string.Empty,
                OpenAiChatCompletionsAdapter.ParseArguments(function?["arguments"])));
        }

        var text = message["content"]?.GetValue<string>() ?? string.Empty;
        return new AiChatResult(text, toolCalls, new AiChatMessage(AiChatRole.Assistant, text, toolCalls, ProviderContent: message.DeepClone()));
    }
}
