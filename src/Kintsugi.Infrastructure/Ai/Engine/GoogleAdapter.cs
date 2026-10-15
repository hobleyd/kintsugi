using System.Text.Json.Nodes;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Ai.Engine;

/// <summary>
/// Gemini's <c>generateContent</c>: Google AI Studio with an API key, or Vertex AI under Google
/// Cloud authentication (<c>…/publishers/google/models/{model}:generateContent</c>).
/// </summary>
/// <remarks>
/// <para>
/// Hosted search here is Google Search grounding (<c>googleSearch</c>), and it is offered
/// <b>instead of</b> function tools, never beside them: most Gemini models refuse a request that
/// mixes grounding with function declarations. A connection with hosted search on therefore
/// researches with grounding alone; one with it off runs on Kintsugi's own tools.
/// </para>
/// <para>
/// A function call has no id on this wire, so one is made from the name and position. Parts that
/// are the model's own reasoning (<c>thought: true</c>) are kept in the replayed turn — with any
/// <c>thoughtSignature</c>, without which Gemini rejects the next request — but never counted as
/// the reply's text.
/// </para>
/// </remarks>
public class GoogleAdapter : IAiProtocolAdapter
{
    public const string AiStudioBaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    private readonly AiHttp _http;

    public GoogleAdapter(AiHttp http)
    {
        _http = http;
    }

    public AiWireProtocol Protocol => AiWireProtocol.Google;

    public async Task<AiChatResult> SendAsync(AiConnectionSettings connection, AiChatRequest request, CancellationToken cancellationToken)
    {
        var contents = new JsonArray();
        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case AiChatRole.User:
                    contents.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray(new JsonObject { ["text"] = message.Text ?? string.Empty }),
                    });
                    break;
                case AiChatRole.Tool:
                    var part = new JsonObject
                    {
                        ["functionResponse"] = new JsonObject
                        {
                            ["name"] = message.ToolName,
                            ["response"] = new JsonObject { ["content"] = message.Text ?? string.Empty },
                        },
                    };
                    // All of one round's responses go in one user turn, in call order.
                    if (contents.Count > 0 && contents[^1] is JsonObject last
                        && last["role"]?.GetValue<string>() == "user" && last["parts"] is JsonArray parts
                        && parts.Count > 0 && parts[0]?["functionResponse"] is not null)
                    {
                        parts.Add(part);
                    }
                    else
                    {
                        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(part) });
                    }

                    break;
                default:
                    contents.Add(new JsonObject
                    {
                        ["role"] = "model",
                        ["parts"] = message.ProviderContent?.DeepClone()
                            ?? new JsonArray(new JsonObject { ["text"] = message.Text ?? string.Empty }),
                    });
                    break;
            }
        }

        var body = new JsonObject { ["contents"] = contents };

        if (request.UseHostedWebSearch)
        {
            body["tools"] = new JsonArray(new JsonObject { ["googleSearch"] = new JsonObject() });
        }
        else if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(new JsonObject
            {
                ["functionDeclarations"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.Parameters.DeepClone(),
                }).ToArray()),
            });
        }

        // MaxOutputTokens is deliberately not sent. On Gemini it caps thinking *and* answer
        // together, and the 2.5+ models think by default: research's 8192 — the ceiling Anthropic
        // and OpenAI always had — let gemini-2.5-flash spend all of it reasoning about Adobe Acrobat
        // and return no text at all (finishReason MAX_TOKENS). The model's own limit (64K for 2.5)
        // is the honest ceiling; a script never comes near it.

        var url = connection.AuthMode == AiAuthMode.GoogleCloud
            ? $"{AiHttp.VertexBaseUrl(connection.GoogleCloudProject!, connection.GoogleCloudLocation!)}/publishers/google/models/{Uri.EscapeDataString(request.Model)}:generateContent"
            : $"{(connection.BaseUrl ?? AiStudioBaseUrl).TrimEnd('/')}/models/{Uri.EscapeDataString(request.Model)}:generateContent";

        var response = await _http.PostJsonAsync(connection, url, body, cancellationToken);

        var candidate = response["candidates"]?[0] as JsonObject;
        var responseParts = candidate?["content"]?["parts"] as JsonArray;
        if (responseParts is null)
        {
            var reason = candidate?["finishReason"]?.GetValue<string>()
                ?? response["promptFeedback"]?["blockReason"]?.GetValue<string>()
                ?? "no candidates";
            throw new ExternalServiceException(reason == "MAX_TOKENS"
                ? $"The AI provider ({connection.Name}) used its whole output budget — on Gemini that includes the model's thinking — before writing any answer (MAX_TOKENS)."
                : $"The AI provider ({connection.Name}) returned no content ({reason}).");
        }

        var text = new List<string>();
        var toolCalls = new List<AiToolCall>();
        foreach (var part in responseParts.OfType<JsonObject>())
        {
            if (part["functionCall"] is JsonObject call)
            {
                var name = call["name"]?.GetValue<string>() ?? string.Empty;
                toolCalls.Add(new AiToolCall(
                    call["id"]?.GetValue<string>() ?? $"{name}-{toolCalls.Count}",
                    name,
                    call["args"] is JsonObject args ? (JsonObject)args.DeepClone() : new JsonObject()));
            }
            else if (part["thought"]?.GetValue<bool>() != true && part["text"]?.GetValue<string>() is { Length: > 0 } t)
            {
                text.Add(t);
            }
        }

        var joined = string.Join("", text);
        return new AiChatResult(joined, toolCalls, new AiChatMessage(AiChatRole.Assistant, joined, toolCalls, ProviderContent: responseParts.DeepClone()));
    }
}
