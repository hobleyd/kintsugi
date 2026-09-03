using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;
using Kintsugi.WebApi.RemoteControl;

namespace Kintsugi.Tests.RemoteControl;

public class RemoteControlSessionBrokerTests
{
    private const string Serial = "C02ABC123DEF";

    /// <summary>Long enough that a busy machine does not fail a test about logic, short enough that
    /// a genuinely stuck relay does not hang the suite.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan LongConsentTimeout = TimeSpan.FromMinutes(5);

    private static RemoteControlSessionBroker CreateBroker() => new(NullLogger<RemoteControlSessionBroker>.Instance);

    [Fact]
    public void IsHostReachable_IsFalseWithNoAgentConnected()
    {
        var broker = CreateBroker();

        Assert.False(broker.IsHostReachable(Serial));
    }

    [Fact]
    public void TryRequestConsent_WithNoAgentConnected_ReportsUnreachable()
    {
        var broker = CreateBroker();

        var outcome = broker.TryRequestConsent(Guid.NewGuid(), Serial, "admin@example.com", LongConsentTimeout);

        Assert.Equal(RemoteControlRequestOutcome.AgentUnreachable, outcome);
    }

    [Fact]
    public async Task TryRequestConsent_AsksTheAgentAndNamesTheRequester()
    {
        var broker = CreateBroker();
        await using var agent = await ConnectAgentAsync(broker);
        var sessionId = Guid.NewGuid();

        var outcome = broker.TryRequestConsent(sessionId, Serial, "admin@example.com", LongConsentTimeout);

        Assert.Equal(RemoteControlRequestOutcome.Requested, outcome);

        var sent = await agent.Socket.ReadSentAsync(Patience);
        Assert.NotNull(sent);

        using var message = JsonDocument.Parse(sent!.Value.Text);
        Assert.Equal("session-requested", message.RootElement.GetProperty("type").GetString());
        Assert.Equal(sessionId, message.RootElement.GetProperty("sessionId").GetGuid());
        // The host user has to be told who is asking, or the dialog is unanswerable.
        Assert.Equal("admin@example.com", message.RootElement.GetProperty("requestedBy").GetString());
    }

    [Fact]
    public async Task TryRequestConsent_IsCaseInsensitiveAboutTheSerialNumber()
    {
        // nginx forwards the certificate CN verbatim and RequireAgentIdentity compares it
        // case-insensitively, so the relay must agree or a lowercase-serial host is unreachable.
        var broker = CreateBroker();
        await using var agent = await ConnectAgentAsync(broker, Serial.ToLowerInvariant());

        Assert.True(broker.IsHostReachable(Serial.ToUpperInvariant()));
        Assert.Equal(
            RemoteControlRequestOutcome.Requested,
            broker.TryRequestConsent(Guid.NewGuid(), Serial.ToUpperInvariant(), "admin@example.com", LongConsentTimeout));
    }

    [Fact]
    public async Task TryRequestConsent_RefusesASecondSessionForTheSameHost()
    {
        var broker = CreateBroker();
        await using var agent = await ConnectAgentAsync(broker);
        broker.TryRequestConsent(Guid.NewGuid(), Serial, "first@example.com", LongConsentTimeout);

        var outcome = broker.TryRequestConsent(Guid.NewGuid(), Serial, "second@example.com", LongConsentTimeout);

        Assert.Equal(RemoteControlRequestOutcome.AlreadyInSession, outcome);
    }

    [Fact]
    public async Task TryRequestConsent_AllowsARetryOnceTheFirstSessionEnded()
    {
        var broker = CreateBroker();
        await using var agent = await ConnectAgentAsync(broker);
        var first = Guid.NewGuid();
        broker.TryRequestConsent(first, Serial, "admin@example.com", LongConsentTimeout);

        broker.EndSession(first, "the administrator disconnected");

        Assert.Equal(
            RemoteControlRequestOutcome.Requested,
            broker.TryRequestConsent(Guid.NewGuid(), Serial, "admin@example.com", LongConsentTimeout));
    }

    [Fact]
    public void GetConsent_ForAnUnknownSession_IsPendingRatherThanGranted()
    {
        // Fails safe across a restart: a poll for a session this process knows nothing about must
        // never read as permission to connect.
        var broker = CreateBroker();

        Assert.Equal(RemoteControlConsent.Pending, broker.GetConsent(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetConsent_LatchesTimedOutOnceTheDialogDeadlinePasses()
    {
        var broker = CreateBroker();
        await using var agent = await ConnectAgentAsync(broker);
        var sessionId = Guid.NewGuid();

        broker.TryRequestConsent(sessionId, Serial, "admin@example.com", TimeSpan.Zero);

        Assert.Equal(RemoteControlConsent.TimedOut, broker.GetConsent(sessionId));
    }

    [Fact]
    public async Task AgentReportingAGrant_IsRecordedAndLeavesTheSessionOpen()
    {
        var broker = CreateBroker();
        var recorded = new List<(Guid SessionId, RemoteControlConsent Outcome)>();
        await using var agent = await ConnectAgentAsync(broker, Serial, onConsent: (id, outcome) =>
        {
            recorded.Add((id, outcome));
            return Task.CompletedTask;
        });

        var sessionId = Guid.NewGuid();
        broker.TryRequestConsent(sessionId, Serial, "admin@example.com", LongConsentTimeout);
        agent.Socket.QueueText(ConsentMessage(sessionId, "Granted"));

        await WaitUntilAsync(() => recorded.Count == 1);

        Assert.Equal((sessionId, RemoteControlConsent.Granted), recorded[0]);
        Assert.Equal(RemoteControlConsent.Granted, broker.GetConsent(sessionId));
    }

    [Fact]
    public async Task AgentReportingARefusal_EndsTheSessionImmediately()
    {
        var broker = CreateBroker();
        var recorded = new List<RemoteControlConsent>();
        await using var agent = await ConnectAgentAsync(broker, Serial, onConsent: (_, outcome) =>
        {
            recorded.Add(outcome);
            return Task.CompletedTask;
        });

        var sessionId = Guid.NewGuid();
        broker.TryRequestConsent(sessionId, Serial, "admin@example.com", LongConsentTimeout);
        agent.Socket.QueueText(ConsentMessage(sessionId, "Denied"));

        await WaitUntilAsync(() => recorded.Count == 1);

        Assert.Equal(RemoteControlConsent.Denied, broker.GetConsent(sessionId));
        Assert.False(broker.IsActive(sessionId));
        // And the host is free to be asked again, rather than stuck behind a dead session.
        Assert.Equal(
            RemoteControlRequestOutcome.Requested,
            broker.TryRequestConsent(Guid.NewGuid(), Serial, "admin@example.com", LongConsentTimeout));
    }

    [Fact]
    public async Task AgentReportingConsentForSomebodyElsesSession_IsIgnored()
    {
        // One enrolled agent must not be able to answer another host's consent dialog.
        var broker = CreateBroker();
        await using var theirs = await ConnectAgentAsync(broker, "OTHERHOST123");
        await using var ours = await ConnectAgentAsync(broker, Serial);

        var sessionId = Guid.NewGuid();
        broker.TryRequestConsent(sessionId, Serial, "admin@example.com", LongConsentTimeout);
        theirs.Socket.QueueText(ConsentMessage(sessionId, "Granted"));

        // Give the receive loop a chance to have acted on it before asserting that it did not.
        await Task.Delay(200);

        Assert.Equal(RemoteControlConsent.Pending, broker.GetConsent(sessionId));
    }

    [Fact]
    public async Task AgentDisconnecting_EndsItsSessionsAndReportsWhy()
    {
        var broker = CreateBroker();
        var endings = new List<(Guid SessionId, string Reason)>();
        var agent = await ConnectAgentAsync(broker, Serial, onSessionEnded: (id, reason) =>
        {
            endings.Add((id, reason));
            return Task.CompletedTask;
        });

        var sessionId = Guid.NewGuid();
        broker.TryRequestConsent(sessionId, Serial, "admin@example.com", LongConsentTimeout);
        agent.Socket.QueueClose();

        await WaitUntilAsync(() => endings.Count == 1);

        Assert.Equal(sessionId, endings[0].SessionId);
        Assert.Contains("disconnected", endings[0].Reason);
        await WaitUntilAsync(() => !broker.IsHostReachable(Serial));

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task AReconnectingAgentReplacesItsOwnSocketRatherThanAddingOne()
    {
        var broker = CreateBroker();
        var first = await ConnectAgentAsync(broker);

        await using var second = await ConnectAgentAsync(broker);

        // Still exactly one reachable host, and a request reaches the new socket.
        Assert.True(broker.IsHostReachable(Serial));
        Assert.Equal(
            RemoteControlRequestOutcome.Requested,
            broker.TryRequestConsent(Guid.NewGuid(), Serial, "admin@example.com", LongConsentTimeout));
        Assert.NotNull(await second.Socket.ReadSentAsync(Patience));

        await first.DisposeAsync();
    }

    [Fact]
    public async Task ARelayedSessionCopiesBytesBothWaysWithoutInterpretingThem()
    {
        // The point of the whole design: the server does not know what a frame or a keystroke is.
        var broker = CreateBroker();
        await using var agent = await ConnectAgentAsync(broker);
        var sessionId = Guid.NewGuid();
        broker.TryRequestConsent(sessionId, Serial, "admin@example.com", LongConsentTimeout);
        agent.Socket.QueueText(ConsentMessage(sessionId, "Granted"));
        await WaitUntilAsync(() => broker.GetConsent(sessionId) == RemoteControlConsent.Granted);

        var agentSide = new FakeWebSocket();
        var viewerSide = new FakeWebSocket();
        var started = new List<Guid>();
        var agentRelay = broker.RunAgentSessionSocketAsync(sessionId, Serial, agentSide, id =>
        {
            started.Add(id);
            return Task.CompletedTask;
        }, CancellationToken.None);
        var viewerRelay = broker.RunViewerSocketAsync(sessionId, viewerSide, _ => Task.CompletedTask, CancellationToken.None);

        await WaitUntilAsync(() => broker.IsActive(sessionId));

        // A "frame" from the agent reaches the viewer unchanged, and still as a binary message...
        agentSide.QueueBinary([0x01, 0x02, 0x03]);
        var atViewer = await viewerSide.ReadSentBytesAsync(Patience);
        Assert.NotNull(atViewer);
        Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Binary, atViewer!.Value.Type);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, atViewer.Value.Payload);

        // ...and a keystroke from the viewer reaches the agent, still as text.
        const string keystroke = "{\"type\":\"key\",\"hid\":4,\"down\":true}";
        viewerSide.QueueText(keystroke);
        var atAgent = await agentSide.ReadSentAsync(Patience);
        Assert.NotNull(atAgent);
        Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Text, atAgent!.Value.Type);
        Assert.Equal(keystroke, atAgent.Value.Text);

        Assert.Equal(new[] { sessionId }, started);

        broker.EndSession(sessionId, "test over");
        await Task.WhenAll(agentRelay, viewerRelay).WaitAsync(Patience);
    }

    [Fact]
    public async Task AViewerCannotConnectToASessionNobodyGranted()
    {
        var broker = CreateBroker();
        await using var agent = await ConnectAgentAsync(broker);
        var sessionId = Guid.NewGuid();
        broker.TryRequestConsent(sessionId, Serial, "admin@example.com", LongConsentTimeout);

        var viewerSide = new FakeWebSocket();
        await broker.RunViewerSocketAsync(sessionId, viewerSide, _ => Task.CompletedTask, CancellationToken.None)
            .WaitAsync(Patience);

        // Returned rather than hanging, and nothing became active.
        Assert.False(broker.IsActive(sessionId));
    }

    [Fact]
    public async Task AnAgentCannotConnectAMediaSocketForAnotherHostsSession()
    {
        var broker = CreateBroker();
        await using var agent = await ConnectAgentAsync(broker);
        var sessionId = Guid.NewGuid();
        broker.TryRequestConsent(sessionId, Serial, "admin@example.com", LongConsentTimeout);
        agent.Socket.QueueText(ConsentMessage(sessionId, "Granted"));
        await WaitUntilAsync(() => broker.GetConsent(sessionId) == RemoteControlConsent.Granted);

        var impostor = new FakeWebSocket();
        await broker.RunAgentSessionSocketAsync(sessionId, "OTHERHOST123", impostor, _ => Task.CompletedTask, CancellationToken.None)
            .WaitAsync(Patience);

        Assert.False(broker.IsActive(sessionId));
    }

    // -------------------------------------------------------------------------------------------

    private static string ConsentMessage(Guid sessionId, string outcome) =>
        JsonSerializer.Serialize(new { type = "consent", sessionId, outcome });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("The condition was still not true after waiting.");
    }

    private static async Task<ConnectedAgent> ConnectAgentAsync(
        RemoteControlSessionBroker broker,
        string serialNumber = Serial,
        Func<Guid, RemoteControlConsent, Task>? onConsent = null,
        Func<Guid, string, Task>? onSessionEnded = null)
    {
        var socket = new FakeWebSocket();
        var cancellation = new CancellationTokenSource();
        var loop = broker.RunAgentControlSocketAsync(
            serialNumber,
            socket,
            onConsent ?? ((_, _) => Task.CompletedTask),
            onSessionEnded ?? ((_, _) => Task.CompletedTask),
            cancellation.Token);

        var agent = new ConnectedAgent(socket, cancellation, loop);
        await WaitUntilAsync(() => broker.IsHostReachable(serialNumber));
        return agent;
    }

    private sealed record ConnectedAgent(FakeWebSocket Socket, CancellationTokenSource Cancellation, Task Loop) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Cancellation.Cancel();

            try
            {
                await Loop.WaitAsync(Patience);
            }
            catch (Exception)
            {
                // The loop is expected to unwind on cancellation; a test must not fail in teardown.
            }

            Cancellation.Dispose();
        }
    }
}
