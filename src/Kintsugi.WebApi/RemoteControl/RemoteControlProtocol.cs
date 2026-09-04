using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kintsugi.WebApi.RemoteControl;

/// <summary>
/// The messages carried on an agent's control socket. Small, JSON, and low-volume: this channel
/// only ever negotiates sessions. Pixels and input go over a separate per-session socket, so a
/// frame stream can never queue up behind a control message or vice versa.
/// </summary>
/// <remarks>
/// <para>
/// Mirrored by hand in <c>clients/macos-agent/src/remote_control.rs</c>, the same way every other
/// request and response struct in the three agents mirrors a C# shape. The discriminator is a
/// <c>type</c> field rather than a wrapper object because the Rust side reads it with serde's
/// internally-tagged enum representation.
/// </para>
/// <para>
/// Note what is deliberately absent: nothing here describes a screen, a frame, a key or a pointer.
/// The media protocol is between the agent and the browser, and this server relays it without
/// looking at it — see <see cref="RemoteControlSessionBroker"/>. That is why adding a capability to
/// the viewer (a second display, a clipboard, a file drop) needs no change on this side at all.
/// </para>
/// </remarks>
public static class RemoteControlProtocol
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public const string SessionRequestedType = "session-requested";
    public const string SessionEndedType = "session-ended";
    public const string ConsentType = "consent";
    public const string HelloType = "hello";

    /// <summary>Server to agent: put the consent dialog up.</summary>
    public sealed record SessionRequested(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("sessionId")] Guid SessionId,
        [property: JsonPropertyName("requestedBy")] string RequestedBy,
        [property: JsonPropertyName("consentTimeoutSeconds")] int ConsentTimeoutSeconds)
    {
        public SessionRequested(Guid sessionId, string requestedBy, int consentTimeoutSeconds)
            : this(SessionRequestedType, sessionId, requestedBy, consentTimeoutSeconds)
        {
        }
    }

    /// <summary>Server to agent: stop capturing. Also sent agent to server, when the person at the
    /// keyboard ends the session from the menu bar.</summary>
    public sealed record SessionEnded(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("sessionId")] Guid SessionId,
        [property: JsonPropertyName("reason")] string Reason)
    {
        public SessionEnded(Guid sessionId, string reason) : this(SessionEndedType, sessionId, reason)
        {
        }
    }

    /// <summary>
    /// Agent to server: whatever the dialog was answered with. <c>outcome</c> is the name of a
    /// <see cref="Domain.Enums.RemoteControlConsent"/> member rather than a boolean, so that "nobody
    /// was there" arrives as itself instead of being flattened into a refusal.
    /// </summary>
    public sealed record ConsentReported(
        [property: JsonPropertyName("sessionId")] Guid SessionId,
        [property: JsonPropertyName("outcome")] string Outcome);

    /// <summary>
    /// Agent to server, once, on connecting. Nothing is required of it — the socket is already
    /// authenticated by the client certificate nginx verified, so this is diagnostics rather than a
    /// handshake, and a socket that never sends one still works.
    /// </summary>
    public sealed record Hello(
        [property: JsonPropertyName("agentVersion")] string? AgentVersion,
        [property: JsonPropertyName("consoleUser")] string? ConsoleUser);

    /// <summary>Reads the <c>type</c> discriminator without committing to a shape, so an unknown
    /// message can be logged and skipped rather than closing the socket.</summary>
    public static string? ReadType(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
