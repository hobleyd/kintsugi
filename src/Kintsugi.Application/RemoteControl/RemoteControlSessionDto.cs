using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.RemoteControl;

/// <summary>
/// What the admin UI needs to drive the remote-control screen: which session it is, whether the
/// host user has answered yet, and — once they have — whether the stream is live.
/// </summary>
/// <remarks>
/// Mirrored by hand in <c>web/lib/data/models/</c>, like every other DTO this client reads.
/// </remarks>
/// <param name="IsActive">True only while both sockets are joined. Distinct from a non-null
/// <see cref="StartedAtUtc"/>, which stays true for a session that has since finished.</param>
public record RemoteControlSessionDto(
    Guid Id,
    Guid? HostId,
    string SerialNumber,
    string Hostname,
    string RequestedBy,
    RemoteControlConsent Consent,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? ConsentDecidedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string? EndReason,
    bool IsActive)
{
    public static RemoteControlSessionDto From(RemoteControlSession session, bool isActive) => new(
        session.Id,
        session.HostId,
        session.SerialNumber,
        session.Hostname,
        session.RequestedBy,
        session.Consent,
        session.CreatedAtUtc,
        session.ConsentDecidedAtUtc,
        session.StartedAtUtc,
        session.EndedAtUtc,
        session.EndReason,
        isActive);
}
