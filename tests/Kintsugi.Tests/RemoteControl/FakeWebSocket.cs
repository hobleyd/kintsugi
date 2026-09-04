using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Kintsugi.Tests.RemoteControl;

/// <summary>
/// A two-ended <see cref="WebSocket"/> stand-in: <see cref="QueueText"/> and friends put messages
/// where <c>ReceiveAsync</c> will find them, and everything the code under test sends is recorded
/// for <see cref="ReadSentAsync"/> to assert on.
/// </summary>
/// <remarks>
/// Only the abstract members are implemented. The <c>Memory&lt;byte&gt;</c> overloads that
/// <c>RemoteControlSessionBroker</c> actually calls are virtual on the base class and delegate to
/// these, so overriding the two <c>ArraySegment</c> members covers both.
/// </remarks>
internal sealed class FakeWebSocket : WebSocket
{
    private readonly Channel<Frame> _inbound = Channel.CreateUnbounded<Frame>();
    private readonly Channel<Frame> _outbound = Channel.CreateUnbounded<Frame>();

    private WebSocketState _state = WebSocketState.Open;

    private sealed record Frame(byte[] Payload, WebSocketMessageType Type, bool EndOfMessage);

    public override WebSocketCloseStatus? CloseStatus { get; }

    public override string? CloseStatusDescription { get; }

    public override WebSocketState State => _state;

    public override string? SubProtocol => null;

    public void QueueText(string text) =>
        _inbound.Writer.TryWrite(new Frame(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true));

    public void QueueBinary(byte[] payload) =>
        _inbound.Writer.TryWrite(new Frame(payload, WebSocketMessageType.Binary, true));

    /// <summary>Makes the peer hang up, which is how the relay learns a side has gone.</summary>
    public void QueueClose() =>
        _inbound.Writer.TryWrite(new Frame([], WebSocketMessageType.Close, true));

    /// <summary>The next message this socket was asked to send, as bytes, or null if none arrived in
    /// time. Kept byte-exact so a relay test can prove nothing was transcoded on the way through.</summary>
    public async Task<(byte[] Payload, WebSocketMessageType Type)?> ReadSentBytesAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);

        try
        {
            var frame = await _outbound.Reader.ReadAsync(cancellation.Token);
            return (frame.Payload, frame.Type);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>As <see cref="ReadSentBytesAsync"/>, decoded as UTF-8 for the JSON control
    /// messages.</summary>
    public async Task<(string Text, WebSocketMessageType Type)?> ReadSentAsync(TimeSpan timeout)
    {
        var sent = await ReadSentBytesAsync(timeout);
        return sent is null ? null : (Encoding.UTF8.GetString(sent.Value.Payload), sent.Value.Type);
    }

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
        _inbound.Writer.TryComplete();
        _outbound.Writer.TryComplete();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;
        _inbound.Writer.TryComplete();
        _outbound.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override void Dispose() => Abort();

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        Frame frame;

        try
        {
            frame = await _inbound.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        if (frame.Type == WebSocketMessageType.Close)
        {
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        frame.Payload.CopyTo(buffer.Array!, buffer.Offset);
        return new WebSocketReceiveResult(frame.Payload.Length, frame.Type, frame.EndOfMessage);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        _outbound.Writer.TryWrite(new Frame(buffer.ToArray(), messageType, endOfMessage));
        return Task.CompletedTask;
    }
}
