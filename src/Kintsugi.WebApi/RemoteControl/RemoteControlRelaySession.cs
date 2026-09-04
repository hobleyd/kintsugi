using System.Net.WebSockets;
using Kintsugi.Domain.Enums;

namespace Kintsugi.WebApi.RemoteControl;

/// <summary>
/// One in-flight remote control session's live state: the consent answer, the two sockets, and the
/// signals each side's request handler waits on. The durable counterpart is
/// <see cref="Domain.Entities.RemoteControlSession"/>; this is gone on restart, deliberately.
/// </summary>
internal sealed class RemoteControlRelaySession
{
    private readonly TaskCompletionSource<WebSocket> _agentSocket = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<WebSocket> _viewerSocket = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed when the relay is over, which is what releases each side's request
    /// handler — an MVC action that returned earlier would have its socket closed underneath it.</summary>
    private readonly TaskCompletionSource _agentSideDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _viewerSideDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly DateTimeOffset _consentDeadlineUtc;
    private readonly object _lock = new();

    private RemoteControlConsent _consent = RemoteControlConsent.Pending;
    private Func<Guid, Task>? _onStarted;
    private int _relayClaimed;
    private int _startedInvoked;

    public RemoteControlRelaySession(Guid id, string serialNumber, string requestedBy, TimeSpan consentTimeout)
    {
        Id = id;
        SerialNumber = serialNumber;
        RequestedBy = requestedBy;
        _consentDeadlineUtc = DateTimeOffset.UtcNow.Add(consentTimeout);
    }

    public Guid Id { get; }

    public string SerialNumber { get; }

    public string RequestedBy { get; }

    public CancellationTokenSource Cancellation { get; } = new();

    public WebSocket? AgentSocket { get; private set; }

    public WebSocket? ViewerSocket { get; private set; }

    public bool IsActive { get; private set; }

    public bool IsFinished => FinishedAtUtc is not null;

    public DateTimeOffset? FinishedAtUtc { get; private set; }

    public string? EndReason { get; private set; }

    public bool MatchesHost(string serialNumber) =>
        string.Equals(SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The consent answer, latching <see cref="RemoteControlConsent.TimedOut"/> once the deadline
    /// has passed. Resolved on read rather than by a timer because the browser is polling for this
    /// answer anyway — a timer would exist only to reach the same conclusion slightly earlier, at
    /// the cost of a background task per request.
    /// </summary>
    public RemoteControlConsent ResolveConsent()
    {
        lock (_lock)
        {
            if (_consent == RemoteControlConsent.Pending && DateTimeOffset.UtcNow > _consentDeadlineUtc)
            {
                _consent = RemoteControlConsent.TimedOut;
            }

            return _consent;
        }
    }

    /// <summary>Records the host user's answer. False means it was already decided — see the same
    /// first-answer-wins rule, and why it is a security property, on the domain entity.</summary>
    public bool LatchConsent(RemoteControlConsent outcome)
    {
        lock (_lock)
        {
            if (_consent != RemoteControlConsent.Pending)
            {
                return false;
            }

            _consent = outcome;
            return true;
        }
    }

    /// <summary>False if this session was already finished, so the caller can skip the teardown it
    /// would otherwise perform twice.</summary>
    public bool Finish(string reason)
    {
        lock (_lock)
        {
            if (FinishedAtUtc is not null)
            {
                return false;
            }

            FinishedAtUtc = DateTimeOffset.UtcNow;
            EndReason = reason;
            IsActive = false;
            return true;
        }
    }

    public void Cancel()
    {
        try
        {
            Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public bool AttachAgentSocket(WebSocket socket)
    {
        if (!_agentSocket.TrySetResult(socket))
        {
            return false;
        }

        AgentSocket = socket;
        return true;
    }

    public bool AttachViewerSocket(WebSocket socket)
    {
        if (!_viewerSocket.TrySetResult(socket))
        {
            return false;
        }

        ViewerSocket = socket;
        return true;
    }

    /// <summary>Takes the first callback offered by either side, since both hand in the same one and
    /// only the relay will call it.</summary>
    public void OfferStartedCallback(Func<Guid, Task> onStarted) => Interlocked.CompareExchange(ref _onStarted, onStarted, null);

    public async Task InvokeStartedAsync(ILogger logger)
    {
        if (Interlocked.Exchange(ref _startedInvoked, 1) == 1)
        {
            return;
        }

        var callback = _onStarted;
        if (callback is null)
        {
            return;
        }

        try
        {
            await callback(Id);
        }
        catch (Exception ex)
        {
            // A session that is already running must not be torn down because its start could not
            // be written to the audit row — the frames are flowing either way, and reporting the
            // failure is more useful than pretending it did not start.
            logger.LogError(ex, "Could not record the start of remote control session {SessionId}", Id);
        }
    }

    /// <summary>True for exactly one caller, so two sockets arriving at once start one relay.</summary>
    public bool TryClaimRelay() => Interlocked.Exchange(ref _relayClaimed, 1) == 0;

    /// <summary>Waits for whichever side has not arrived yet. False on timeout.</summary>
    public async Task<bool> WaitForBothSocketsAsync(TimeSpan timeout)
    {
        var both = Task.WhenAll(_agentSocket.Task, _viewerSocket.Task);
        var completed = await Task.WhenAny(both, Task.Delay(timeout, Cancellation.Token)).ConfigureAwait(false);
        return completed == both;
    }

    public void MarkActive()
    {
        lock (_lock)
        {
            if (FinishedAtUtc is null)
            {
                IsActive = true;
            }
        }
    }

    public Task WaitForAgentSideAsync(CancellationToken cancellationToken) =>
        WaitAsync(_agentSideDone, cancellationToken);

    public Task WaitForViewerSideAsync(CancellationToken cancellationToken) =>
        WaitAsync(_viewerSideDone, cancellationToken);

    /// <summary>Releases both request handlers. Always called from the relay's own finally, so a
    /// socket action never returns while its socket is still being copied.</summary>
    public void ReleaseBothSides()
    {
        _agentSideDone.TrySetResult();
        _viewerSideDone.TrySetResult();
    }

    private async Task WaitAsync(TaskCompletionSource done, CancellationToken cancellationToken)
    {
        // A request aborted at the HTTP layer (the browser closed the tab, the agent's process
        // exited) cancels the whole session rather than only this side of it: the relay is a pair,
        // and half of one is of no use to anybody.
        await using var registration = cancellationToken.Register(Cancel);
        await done.Task.ConfigureAwait(false);
    }
}
