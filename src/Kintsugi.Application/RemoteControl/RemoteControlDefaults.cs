namespace Kintsugi.Application.RemoteControl;

public static class RemoteControlDefaults
{
    /// <summary>
    /// How long the consent dialog stays up before it gives up and the request is recorded as
    /// <c>TimedOut</c>. Kept in step with the agent's own dialog timeout
    /// (<c>remote_control::CONSENT_TIMEOUT</c> in clients/macos-agent) — the agent's is what the
    /// user actually experiences, and this is the server's backstop for an agent that never answers
    /// at all, so this must be the longer of the two or the server would abandon a dialog that is
    /// still on screen.
    /// </summary>
    public static readonly TimeSpan ConsentTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long a granted session waits for both sockets to turn up before it is abandoned. Covers
    /// the ordinary case (the agent connects in well under a second) with room for a slow link, and
    /// bounds the case that matters: consent granted and the administrator closing the tab, which
    /// would otherwise leave the agent capturing to nobody.
    /// </summary>
    public static readonly TimeSpan PairingTimeout = TimeSpan.FromSeconds(30);
}
