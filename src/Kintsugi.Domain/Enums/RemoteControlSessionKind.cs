using System.Text.Json.Serialization;

namespace Kintsugi.Domain.Enums;

/// <summary>
/// What an administrator asked for. The two kinds share every piece of machinery below this
/// point — the same control socket, the same per-session socket, the same relay, the same audit
/// row — and differ only in what the agent does once a session is open.
/// </summary>
/// <remarks>
/// <para>
/// Carries a converter, so it crosses the wire as a name, for the same reason
/// <see cref="RemoteControlConsent"/> does: no agent reads this as an ordinal. The agents parse it
/// from the <c>kind</c> field of <c>session-requested</c> as a string, and an agent too old to know
/// the field at all reads its absence as <see cref="Screen"/> — which is the only thing it could
/// have meant before shell sessions existed. See the enum note in CLAUDE.md before adding another.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RemoteControlSessionKind
{
    /// <summary>
    /// Remote control as it has always been: the host user is asked, and a grant streams the screen
    /// and accepts pointer and keyboard input.
    /// </summary>
    Screen,

    /// <summary>
    /// An interactive terminal on the host — what an administrator would otherwise reach for SSH or
    /// a remote PowerShell session to get, without an inbound port on every managed machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Nobody at the host is asked, and that is the deliberate difference.</strong> A shell
    /// session reports <see cref="RemoteControlConsent.NotRequired"/> rather than putting a dialog
    /// up, because it is the equivalent of the SSH access a fleet administrator already has by
    /// other means, and because the hosts most likely to need one — Linux servers — have nobody
    /// sitting at them to ask. The compensating control is that a shell request writes exactly the
    /// same <c>remote_control_sessions</c> row a screen request does, naming who asked and when.
    /// </para>
    /// <para>
    /// The shell runs as the account that already holds this host's fleet identity, which is
    /// <c>root</c> on macOS and Linux and <c>SYSTEM</c> on Windows — never the logged-in user, on
    /// any platform. It is stated in the viewer rather than left to be remembered, because a shell
    /// running as somebody's own account would be a materially different thing to hand out and
    /// should be visible as such if one ever appeared.</para>
    /// <para>
    /// macOS reaches that through a handoff the other two do not need: remote control lives in its
    /// per-user process (the screen belongs to a GUI session), so that process answers the request
    /// and asks a root LaunchDaemon to open the terminal and the media socket. See
    /// <c>clients/macos-agent/src/remote_shell.rs</c>.
    /// </para>
    /// </remarks>
    Shell
}
