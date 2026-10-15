using System.Text.Json.Nodes;
using Kintsugi.Application.AiSettings;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Ai.Engine;

/// <summary>
/// <c>POST {base}/responses</c> — OpenAI's newer shape, and the one its hosted web search lives on.
/// What the <see cref="AiProvider.OpenAI"/> provider has always used.
/// </summary>
public class OpenAiResponsesAdapter : IAiProtocolAdapter
{
    private readonly AiHttp _http;

    public OpenAiResponsesAdapter(AiHttp http)
    {
        _http = http;
    }

    public AiWireProtocol Protocol => AiWireProtocol.OpenAiResponses;

    public async Task<AiChatResult> SendAsync(AiConnectionSettings connection, AiChatRequest request, CancellationToken cancellationToken)
    {
        var input = new JsonArray();
        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case AiChatRole.User:
                    input.Add(new JsonObject { ["role"] = "user", ["content"] = message.Text ?? string.Empty });
                    break;
                case AiChatRole.Tool:
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = message.ToolCallId,
                        ["output"] = message.Text ?? string.Empty,
                    });
                    break;
                default:
                    // The output items as returned — reasoning items and function calls included —
                    // which is how this API expects a prior turn to be carried forward.
                    if (message.ProviderContent is JsonArray items)
                    {
                        foreach (var item in items)
                        {
                            input.Add(item?.DeepClone());
                        }
                    }
                    else
                    {
                        input.Add(new JsonObject { ["role"] = "assistant", ["content"] = message.Text ?? string.Empty });
                    }

                    break;
            }
        }

        var tools = new JsonArray();
        if (request.UseHostedWebSearch)
        {
            tools.Add(new JsonObject { ["type"] = "web_search_preview" });
        }

        foreach (var tool in request.Tools)
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = tool.Parameters.DeepClone(),
            });
        }

        var body = new JsonObject { ["model"] = request.Model, ["input"] = input };
        if (tools.Count > 0)
        {
            body["tools"] = tools;
        }

        if (request.MaxOutputTokens is { } max)
        {
            body["max_output_tokens"] = max;
        }

        var url = (connection.BaseUrl ?? OpenAiChatCompletionsAdapter.DefaultBaseUrl).TrimEnd('/') + "/responses";
        var response = await _http.PostJsonAsync(connection, url, body, cancellationToken);

        var output = response["output"] as JsonArray ?? new JsonArray();
        var text = new List<string>();
        var toolCalls = new List<AiToolCall>();
        foreach (var item in output.OfType<JsonObject>())
        {
            switch (item["type"]?.GetValue<string>())
            {
                case "message":
                    foreach (var part in (item["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        if (part["type"]?.GetValue<string>() == "output_text" && part["text"]?.GetValue<string>() is { Length: > 0 } t)
                        {
                            text.Add(t);
                        }
                    }

                    break;
                case "function_call":
                    toolCalls.Add(new AiToolCall(
                        item["call_id"]?.GetValue<string>() ?? $"call_{toolCalls.Count}",
                        item["name"]?.GetValue<string>() ?? string.Empty,
                        OpenAiChatCompletionsAdapter.ParseArguments(item["arguments"])));
                    break;
            }
        }

        var joined = string.Join("\n", text);
        return new AiChatResult(joined, toolCalls, new AiChatMessage(AiChatRole.Assistant, joined, toolCalls, ProviderContent: output.DeepClone()));
    }
}
