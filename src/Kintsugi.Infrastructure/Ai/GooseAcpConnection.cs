using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Kintsugi.Application.Common.Exceptions;

namespace Kintsugi.Infrastructure.Ai;

/// <summary>
/// A minimal client for the Agent Client Protocol (ACP, https://agentclientprotocol.com) as
/// exposed by <c>goose serve</c>'s WebSocket transport — one JSON-RPC message per WebSocket text
/// frame, over a single persistent connection to <c>ws(s)://host:port/acp</c>. Verified directly
/// against a running instance: `goose serve` also exposes an HTTP+SSE variant (`POST`/`GET /acp`)
/// that accepts requests and creates sessions correctly but never actually dispatches
/// <c>session/prompt</c> to the agent loop in the version this was built against — a real gap in
/// that transport, confirmed by the fact that Goose Desktop (which uses WebSocket) works
/// correctly against the exact same server. WebSocket is therefore the only transport this client
/// uses. No file-system or terminal callbacks are advertised, since the Goose process this
/// ultimately talks to runs its own tools against its own machine, not through this client.
/// </summary>
internal sealed class GooseAcpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _webSocket;
    private readonly ILogger _logger;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _receiveLoopCts = new();

    private int _nextId;
    private Task? _receiveLoopTask;
    private Action<JsonElement>? _onSessionUpdate;

    private GooseAcpConnection(ClientWebSocket webSocket, ILogger logger)
    {
        _webSocket = webSocket;
        _logger = logger;
    }

    public static async Task<GooseAcpConnection> ConnectAsync(Uri baseUri, string? secretKey, ILogger logger, CancellationToken cancellationToken)
    {
        var webSocket = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(secretKey))
        {
            webSocket.Options.SetRequestHeader("X-Secret-Key", secretKey);
        }

        try
        {
            await webSocket.ConnectAsync(ToWebSocketUri(baseUri), cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
        {
            webSocket.Dispose();
            throw new ExternalServiceException($"Could not connect to the Goose serve WebSocket endpoint: {ex.Message}", ex);
        }

        var connection = new GooseAcpConnection(webSocket, logger);
        connection._receiveLoopTask = Task.Run(() => connection.ReceiveLoopAsync(connection._receiveLoopCts.Token));
        return connection;
    }

    private static Uri ToWebSocketUri(Uri baseUri)
    {
        var acpUri = new Uri(baseUri, "acp");
        var builder = new UriBuilder(acpUri) { Scheme = acpUri.Scheme == "https" ? "wss" : "ws" };
        return builder.Uri;
    }

    /// <summary>Performs the ACP handshake and returns the agent's advertised name/version, if any.</summary>
    public async Task<(string? Name, string? Version)> InitializeAsync(CancellationToken cancellationToken)
    {
        var id = NextId();
        var request = new
        {
            jsonrpc = "2.0",
            id,
            method = "initialize",
            @params = new
            {
                protocolVersion = 1,
                clientCapabilities = new { },
                clientInfo = new { name = "kintsugi-patching-system", title = "Kintsugi Patching System", version = "1.0" }
            }
        };

        var result = await SendRequestAsync(id, request, cancellationToken);

        string? name = null;
        string? version = null;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("agentInfo", out var agentInfo))
        {
            name = agentInfo.TryGetProperty("name", out var n) ? n.GetString() : null;
            version = agentInfo.TryGetProperty("version", out var v) ? v.GetString() : null;
        }

        return (name, version);
    }

    /// <summary>Creates a session and, when <paramref name="model"/> is set, best-effort switches to
    /// a matching "model" config option the agent advertised — Goose's model selection isn't a free
    /// text field over ACP, so this silently keeps the agent's own default when nothing matches.</summary>
    public async Task<string> CreateSessionAsync(string? model, CancellationToken cancellationToken)
    {
        var id = NextId();
        var request = new
        {
            jsonrpc = "2.0",
            id,
            method = "session/new",
            @params = new { cwd = "/", mcpServers = Array.Empty<object>() }
        };

        var result = await SendRequestAsync(id, request, cancellationToken);

        var sessionId = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("sessionId", out var sessionIdProp)
            ? sessionIdProp.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ExternalServiceException("Goose did not return a session ID.");
        }

        if (!string.IsNullOrWhiteSpace(model) && result.TryGetProperty("configOptions", out var configOptions))
        {
            var match = FindModelConfigOptionId(configOptions, model);
            if (match is { } option)
            {
                var setConfigId = NextId();
                await SendRequestAsync(setConfigId, new
                {
                    jsonrpc = "2.0",
                    id = setConfigId,
                    method = "session/set_config_option",
                    @params = new { sessionId, configId = option.ConfigId, value = option.Value }
                }, cancellationToken);
            }
        }

        return sessionId;
    }

    /// <summary>Sends a single prompt and collects the agent's text reply, automatically resolving
    /// any tool-permission prompts along the way (there's no human present in this headless flow)
    /// until the turn ends.</summary>
    public async Task<string> PromptAsync(string sessionId, string prompt, CancellationToken cancellationToken)
    {
        var responseText = new StringBuilder();
        _onSessionUpdate = notification =>
        {
            LogSessionUpdate(notification, sessionId);
            AppendAgentMessageText(notification, sessionId, responseText);
        };

        _logger.LogInformation("Goose session {SessionId}: sending prompt ({PromptLength} chars) at {ElapsedSeconds}s", sessionId, prompt.Length, _stopwatch.Elapsed.TotalSeconds);

        try
        {
            var id = NextId();
            var request = new
            {
                jsonrpc = "2.0",
                id,
                method = "session/prompt",
                @params = new
                {
                    sessionId,
                    prompt = new object[] { new { type = "text", text = prompt } }
                }
            };

            var result = await SendRequestAsync(id, request, cancellationToken);
            var stopReason = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("stopReason", out var stopReasonEl)
                ? stopReasonEl.GetString()
                : null;
            _logger.LogInformation("Goose session {SessionId}: prompt turn ended ({StopReason}) at {ElapsedSeconds}s", sessionId, stopReason ?? "unknown", _stopwatch.Elapsed.TotalSeconds);
        }
        finally
        {
            _onSessionUpdate = null;
        }

        return responseText.ToString();
    }

    private void LogSessionUpdate(JsonElement notification, string sessionId)
    {
        if (!notification.TryGetProperty("params", out var paramsEl) ||
            !paramsEl.TryGetProperty("update", out var update) ||
            !update.TryGetProperty("sessionUpdate", out var kindEl))
        {
            return;
        }

        var kind = kindEl.GetString();
        var detail = kind switch
        {
            "tool_call" or "tool_call_update" =>
                (update.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null) is { } title
                    ? $" — {title}" + (update.TryGetProperty("status", out var statusEl) ? $" ({statusEl.GetString()})" : "")
                    : update.TryGetProperty("status", out var statusOnlyEl) ? $" ({statusOnlyEl.GetString()})" : "",
            "plan" when update.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array =>
                $" — {entriesEl.GetArrayLength()} step(s)",
            "usage_update" when update.TryGetProperty("used", out var usedEl) && update.TryGetProperty("size", out var sizeEl) =>
                $" — {usedEl}/{sizeEl} tokens",
            _ => ""
        };

        _logger.LogInformation("Goose session {SessionId}: {Kind}{Detail} at {ElapsedSeconds}s", sessionId, kind, detail, _stopwatch.Elapsed.TotalSeconds);
    }

    private static void AppendAgentMessageText(JsonElement notification, string sessionId, StringBuilder buffer)
    {
        if (!notification.TryGetProperty("params", out var paramsEl) ||
            !paramsEl.TryGetProperty("sessionId", out var sessionIdEl) || sessionIdEl.GetString() != sessionId ||
            !paramsEl.TryGetProperty("update", out var update) ||
            !update.TryGetProperty("sessionUpdate", out var kindEl) || kindEl.GetString() != "agent_message_chunk" ||
            !update.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("text", out var textEl))
        {
            return;
        }

        buffer.Append(textEl.GetString());
    }

    private async Task RespondToPermissionRequestAsync(JsonElement id, JsonElement request, CancellationToken cancellationToken)
    {
        string? optionId = null;

        if (request.TryGetProperty("params", out var paramsEl) &&
            paramsEl.TryGetProperty("options", out var options) &&
            options.ValueKind == JsonValueKind.Array)
        {
            optionId = FindPermissionOptionId(options, "allow_once")
                ?? FindPermissionOptionId(options, "allow_always");
        }

        _logger.LogInformation(
            "Goose requested tool permission at {ElapsedSeconds}s — resolving to {Outcome}",
            _stopwatch.Elapsed.TotalSeconds, optionId is not null ? $"selected:{optionId}" : "cancelled");

        object outcome = optionId is not null
            ? new { outcome = "selected", optionId }
            : new { outcome = "cancelled" };

        try
        {
            await SendMessageAsync(new { jsonrpc = "2.0", id, result = new { outcome } }, cancellationToken);
        }
        catch (Exception)
        {
            // Best-effort acknowledgement — a failure here shouldn't abort an otherwise-successful
            // prompt turn; worst case the agent's own permission timeout handles it.
        }
    }

    private static string? FindPermissionOptionId(JsonElement options, string kind)
    {
        foreach (var option in options.EnumerateArray())
        {
            if (option.TryGetProperty("kind", out var kindEl) && kindEl.GetString() == kind &&
                option.TryGetProperty("optionId", out var optionIdEl))
            {
                return optionIdEl.GetString();
            }
        }

        return null;
    }

    private static (string ConfigId, string Value)? FindModelConfigOptionId(JsonElement configOptions, string model)
    {
        if (configOptions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var configOption in configOptions.EnumerateArray())
        {
            if (!configOption.TryGetProperty("category", out var categoryEl) || categoryEl.GetString() != "model" ||
                !configOption.TryGetProperty("id", out var configIdEl) ||
                !configOption.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var option in options.EnumerateArray())
            {
                var value = option.TryGetProperty("value", out var valueEl) ? valueEl.GetString() : null;
                var name = option.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;

                if (string.Equals(value, model, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, model, StringComparison.OrdinalIgnoreCase))
                {
                    return (configIdEl.GetString()!, value ?? model);
                }
            }
        }

        return null;
    }

    private async Task<JsonElement> SendRequestAsync(int id, object request, CancellationToken cancellationToken)
    {
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;

        try
        {
            await SendMessageAsync(request, cancellationToken);
        }
        catch (Exception)
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        JsonElement message;
        try
        {
            message = await pending.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }

        ThrowIfError(message);
        return message.TryGetProperty("result", out var result) ? result : default;
    }

    private async Task SendMessageAsync(object message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);

        try
        {
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        catch (WebSocketException ex)
        {
            throw new ExternalServiceException($"Could not send to the Goose WebSocket: {ex.Message}", ex);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];

        try
        {
            while (_webSocket.State == WebSocketState.Open)
            {
                using var messageStream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogWarning("Goose WebSocket closed by server at {ElapsedSeconds}s", _stopwatch.Elapsed.TotalSeconds);
                        FaultAllPending(new ExternalServiceException("The Goose WebSocket connection was closed."));
                        return;
                    }

                    messageStream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                messageStream.Position = 0;
                using var document = await JsonDocument.ParseAsync(messageStream, cancellationToken: cancellationToken);
                await Dispatch(document.RootElement, cancellationToken);
            }

            FaultAllPending(new ExternalServiceException("The Goose WebSocket connection ended before all responses arrived."));
        }
        catch (OperationCanceledException)
        {
            // Expected on disposal.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Goose WebSocket receive loop failed at {ElapsedSeconds}s", _stopwatch.Elapsed.TotalSeconds);
            FaultAllPending(ex);
        }

        async Task Dispatch(JsonElement root, CancellationToken ct)
        {
            if (root.TryGetProperty("method", out var methodProp))
            {
                switch (methodProp.GetString())
                {
                    case "session/update":
                        _onSessionUpdate?.Invoke(root.Clone());
                        break;
                    case "session/request_permission" when root.TryGetProperty("id", out var permissionId):
                        await RespondToPermissionRequestAsync(permissionId.Clone(), root, ct);
                        break;
                }

                return;
            }

            if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number &&
                _pending.TryGetValue(idProp.GetInt32(), out var tcs))
            {
                tcs.TrySetResult(root.Clone());
            }
        }
    }

    private void FaultAllPending(Exception exception)
    {
        foreach (var id in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(id, out var tcs))
            {
                tcs.TrySetException(exception);
            }
        }
    }

    private static void ThrowIfError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error))
        {
            return;
        }

        var message = error.TryGetProperty("message", out var messageEl) ? messageEl.GetString() : null;
        var detail = error.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.String
            ? dataEl.GetString()
            : null;

        var text = string.IsNullOrWhiteSpace(detail) ? message ?? "unknown error" : $"{message}: {detail}";
        throw new ExternalServiceException($"Goose returned an error: {text}");
    }

    private int NextId() => Interlocked.Increment(ref _nextId);

    public async ValueTask DisposeAsync()
    {
        _receiveLoopCts.Cancel();
        FaultAllPending(new ObjectDisposedException(nameof(GooseAcpConnection)));

        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        }
        catch
        {
            // Best-effort — the connection may already be broken.
        }

        _webSocket.Dispose();
        _receiveLoopCts.Dispose();
    }
}
