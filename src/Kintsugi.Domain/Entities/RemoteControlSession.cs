using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// One administrator's request to take remote control of one host, from the moment it was asked
/// through to whatever became of it. This is the audit trail, not the live session: the sockets,
/// the frames and the input all live in memory in <c>RemoteControlSessionBroker</c> and are gone
/// the instant the process restarts. What must survive is *who asked to watch whose screen, and
/// whether they were allowed to*.
/// </summary>
/// <remarks>
/// <para>
/// A row is written when the request is made, before anyone has consented to anything, which is
/// deliberate: "an administrator asked to control this laptop and was refused" is precisely the
/// event an auditor is looking for, and a design that only recorded successful sessions would
/// throw it away. The same reasoning covers <see cref="RemoteControlConsent.AgentUnreachable"/> —
/// an attempt on a host that never answered is still an attempt.
/// </para>
/// <para>
/// There is no foreign key to <see cref="Host"/>, and the serial number and hostname are copied in
/// rather than read through one. Removing a host hard-deletes its row once the agent confirms (see
/// <c>ConfirmHostRemovalCommandHandler</c>), and an audit record that vanished when its subject was
/// decommissioned would be worthless — the same reasoning as the Vanta sync keying on derived
/// values rather than row identity. <see cref="HostId"/> is kept only so the UI can offer a link
/// back while the host still exists, and is nullable for the same reason.
/// </para>
/// </remarks>
public class RemoteControlSession : BaseEntity
{
    /// <summary>The host this request was for, while it still exists. Nullable on purpose — see
    /// the note on this class about outliving its subject.</summary>
    public Guid? HostId { get; private set; }

    /// <summary>
    /// The host's serial number: this system's real host identity (it is the agent certificate's
    /// CN), and the value the relay matches an arriving agent socket against.
    /// </summary>
    public string SerialNumber { get; private set; } = default!;

    /// <summary>The hostname as it read at request time, copied in so the record still names
    /// something a human recognises after the host is gone or renamed.</summary>
    public string Hostname { get; private set; } = default!;

    /// <summary>
    /// Who asked — the signed-in administrator's own identity, taken from the session cookie's
    /// claims by the controller and never from anything the caller sent. On a server running with
    /// authentication deliberately disabled there is no identity to record, and this says so
    /// rather than inventing one.
    /// </summary>
    public string RequestedBy { get; private set; } = default!;

    public RemoteControlConsent Consent { get; private set; } = RemoteControlConsent.Pending;

    public DateTimeOffset? ConsentDecidedAtUtc { get; private set; }

    /// <summary>When pixels actually started flowing, which is later than consent and may never
    /// happen at all (consent granted, then the viewer closed the tab).</summary>
    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? EndedAtUtc { get; private set; }

    /// <summary>Why the session finished — "viewer disconnected", "the user pressed Disconnect",
    /// "the agent went away". Free text, because the interesting cases are the ones nobody
    /// enumerated in advance.</summary>
    public string? EndReason { get; private set; }

    private RemoteControlSession()
    {
    }

    public static RemoteControlSession Request(Guid? hostId, string serialNumber, string hostname, string requestedBy)
    {
        if (string.IsNullOrWhiteSpace(serialNumber))
        {
            throw new DomainException("Serial number is required.");
        }

        if (string.IsNullOrWhiteSpace(hostname))
        {
            throw new DomainException("Hostname is required.");
        }

        if (string.IsNullOrWhiteSpace(requestedBy))
        {
            throw new DomainException("The requesting administrator is required.");
        }

        return new RemoteControlSession
        {
            HostId = hostId,
            SerialNumber = serialNumber.Trim(),
            Hostname = hostname.Trim(),
            RequestedBy = requestedBy.Trim()
        };
    }

    /// <summary>
    /// Records the answer, once. A second attempt to decide an already-decided request is ignored
    /// rather than applied, and that is a security invariant rather than defensiveness: the
    /// consent message arrives over a socket held by the agent, so without this a host that had
    /// already refused (or timed out) could be talked into sending a second, granting message and
    /// overwrite the refusal. First answer wins.
    /// </summary>
    public void RecordConsent(RemoteControlConsent outcome)
    {
        if (outcome == RemoteControlConsent.Pending)
        {
            throw new DomainException("Pending is not a decision.");
        }

        if (Consent != RemoteControlConsent.Pending)
        {
            return;
        }

        Consent = outcome;
        ConsentDecidedAtUtc = DateTimeOffset.UtcNow;
        MarkUpdated();
    }

    /// <summary>Marks the point the two sockets were joined and frames began. Refuses on a request
    /// nobody granted, so a bug in the relay cannot produce a record of a session that ran without
    /// consent.</summary>
    public void MarkStarted()
    {
        if (Consent != RemoteControlConsent.Granted)
        {
            throw new DomainException("A remote control session cannot start without the host user's consent.");
        }

        if (StartedAtUtc is not null)
        {
            return;
        }

        StartedAtUtc = DateTimeOffset.UtcNow;
        MarkUpdated();
    }

    /// <summary>
    /// Closes the record out. Idempotent, and the first reason wins — both socket handlers race to
    /// call this as the relay unwinds (see <c>RemoteControlSessionBroker</c>), and the side that
    /// noticed first is the one that knows why.
    /// </summary>
    public void MarkEnded(string reason)
    {
        if (EndedAtUtc is not null)
        {
            return;
        }

        EndedAtUtc = DateTimeOffset.UtcNow;
        EndReason = string.IsNullOrWhiteSpace(reason) ? "ended" : reason.Trim();
        MarkUpdated();
    }
}
