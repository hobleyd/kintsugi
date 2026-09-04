using System.Text.Json.Serialization;

namespace Kintsugi.Domain.Enums;

/// <summary>
/// What the person sitting at a host said when an administrator asked to control it — the one
/// question this whole feature turns on, so it is recorded for every request rather than only for
/// the ones that went ahead.
/// </summary>
/// <remarks>
/// Carries a converter, so it crosses the wire as a name. That is safe here in a way it is not for
/// <c>HostStatus</c> or <c>PatchingTimeUnit</c>: no agent parses this value at all (the agent is
/// the one *reporting* it, and reports a boolean), so there is no <c>policy.rs</c>-style ordinal
/// reader to break. See the enum note in CLAUDE.md before adding another.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RemoteControlConsent
{
    /// <summary>The dialog is on screen (or about to be) and nobody has answered yet.</summary>
    Pending,

    /// <summary>The person at the keyboard allowed it.</summary>
    Granted,

    /// <summary>The person at the keyboard refused.</summary>
    Denied,

    /// <summary>
    /// Nobody answered before the dialog gave up. Deliberately distinct from <see cref="Denied"/>
    /// in the record even though it has the same effect, because "refused" and "was away from the
    /// desk" are different facts about a host and an auditor reading this will want to tell them
    /// apart.
    /// </summary>
    TimedOut,

    /// <summary>
    /// No agent was holding a control socket for this host when the request was made, so nobody
    /// was ever asked. An enrolled host that is switched off, asleep, or has no logged-in user
    /// looks exactly like this.
    /// </summary>
    AgentUnreachable
}
