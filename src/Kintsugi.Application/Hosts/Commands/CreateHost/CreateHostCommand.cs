using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Hosts.Commands.CreateHost;

// CheckInMinute: the minute-of-hour (0-59) this host currently checks in on — see
// clients/macos-agent/src/checkin_schedule.rs. Fed to ICheckInLoadBalancer so the response can
// tell the host to move to a different minute if this one is overloaded.
//
// AgentVersion: the reporting agent's own build version (CARGO_PKG_VERSION, sent by every
// agent's RegisterHostRequest). Optional only so that agents predating the field still check in;
// current builds always send it.
public record CreateHostCommand(
    string Hostname,
    string SerialNumber,
    int CheckInMinute = 0,
    string? OperatingSystem = null,
    string? IpAddress = null,
    bool? OperatingSystemUpdateAvailable = null,
    string? OperatingSystemLatestVersion = null,
    string? AgentVersion = null) : IRequest<CreateHostResult>, IAgentScopedRequest;

/// <summary>
/// <paramref name="SuggestedCheckInMinute"/> is only ever non-null when
/// <see cref="ICheckInLoadBalancer"/> decides this host's current check-in minute is carrying more
/// load than others — the agent persists it and reschedules itself accordingly (see
/// checkin_schedule::apply). Deliberately not folded into <see cref="HostDto"/>: that's a durable
/// resource shape reused by the hosts listing/detail endpoints, where a per-check-in suggestion
/// wouldn't mean anything.
/// </summary>
public record CreateHostResult(HostDto Host, bool WasCreated, int? SuggestedCheckInMinute);
