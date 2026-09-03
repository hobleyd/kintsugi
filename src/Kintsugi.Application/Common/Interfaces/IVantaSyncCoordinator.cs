using Kintsugi.Application.Vanta;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// The narrow, Application-layer view of the background Vanta sync: ask it to run, and read what it
/// last did. The concrete coordinator carries the writer-side methods the hosted service needs and
/// is registered separately for it — the shape the three upgrade-path coordinators already follow.
/// </summary>
public interface IVantaSyncCoordinator
{
    /// <summary>
    /// Asks for a run. False when one is already in flight, and that rejection matters more here
    /// than it does for the other coordinators: Vanta revokes an application's previous access token
    /// the moment a new one is issued, so two overlapping runs would take turns invalidating each
    /// other's credentials mid-upload.
    /// </summary>
    bool TryRequestStart();

    VantaSyncStatusDto GetStatus();
}
