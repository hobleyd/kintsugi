using System.Text.Json;
using Kintsugi.WebApi.RemoteControl;

namespace Kintsugi.Tests.RemoteControl;

/// <summary>
/// The control-socket messages are deserialized from what the agents' <c>remote_protocol.rs</c>
/// writes. A shape System.Text.Json cannot construct is not a compile error — it throws on the
/// first message of that type, which for <c>sessionEnded</c> meant tearing down the host's control
/// socket every time a session finished.
/// </summary>
public class RemoteControlProtocolTests
{
    [Fact]
    public void SessionEnded_DeserializesWhatTheAgentSends()
    {
        // Verbatim shape from AgentMessage::SessionEnded in clients/*/src/remote_protocol.rs.
        const string json = """{"type":"session-ended","sessionId":"8a9e59e8-8190-4ef0-9fce-5c6c76776c77","reason":"the session was ended on the host"}""";

        var ended = JsonSerializer.Deserialize<RemoteControlProtocol.SessionEnded>(json, RemoteControlProtocol.Json);

        Assert.NotNull(ended);
        Assert.Equal(RemoteControlProtocol.SessionEndedType, ended.Type);
        Assert.Equal(Guid.Parse("8a9e59e8-8190-4ef0-9fce-5c6c76776c77"), ended.SessionId);
        Assert.Equal("the session was ended on the host", ended.Reason);
    }

    [Fact]
    public void SessionRequested_RoundTripsThroughItsConvenienceConstructor()
    {
        var sessionId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(
            new RemoteControlProtocol.SessionRequested(sessionId, "admin@example.com", 90),
            RemoteControlProtocol.Json);

        var requested = JsonSerializer.Deserialize<RemoteControlProtocol.SessionRequested>(json, RemoteControlProtocol.Json);

        Assert.NotNull(requested);
        Assert.Equal(RemoteControlProtocol.SessionRequestedType, requested.Type);
        Assert.Equal(sessionId, requested.SessionId);
        Assert.Equal("admin@example.com", requested.RequestedBy);
        Assert.Equal(90, requested.ConsentTimeoutSeconds);
    }
}
