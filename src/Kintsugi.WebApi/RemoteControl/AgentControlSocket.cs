using System.Threading.Channels;

namespace Kintsugi.WebApi.RemoteControl;

/// <summary>
/// One agent's standing control socket, from the relay's side: a send queue and a way to hang up.
/// </summary>
/// <remarks>
/// The queue exists because <c>WebSocket.SendAsync</c> forbids concurrent sends, and a session
/// request originates on whichever request thread called
/// <c>RemoteControlSessionBroker.TryRequestConsent</c> — potentially several at once, for several
/// hosts, none of them the thread holding this socket. Bounded rather than unbounded: this channel
/// only ever carries session negotiation, so a queue this deep means the agent has stopped reading,
/// and the right answer then is to report the host unreachable rather than to buffer for it.
/// </remarks>
internal sealed class AgentControlSocket
{
    private const int OutboundCapacity = 32;

    public AgentControlSocket(string serialNumber)
    {
        SerialNumber = serialNumber;
        Outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(OutboundCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public string SerialNumber { get; }

    public Channel<string> Outbound { get; }

    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>Queues a message without ever blocking the caller. False means the socket is
    /// closing or the agent has stopped reading — either way, unreachable.</summary>
    public bool TrySend(string message) => Outbound.Writer.TryWrite(message);

    public void Cancel()
    {
        Outbound.Writer.TryComplete();

        try
        {
            Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
