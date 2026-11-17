using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.DeviceHealth.Commands.ReportHostHealthEvents;

/// <param name="Kind">"UnexpectedShutdown", "HardwareErrorFatal" or "HardwareErrorCorrected".</param>
/// <param name="Source">Where the operating system logged it ("Kernel-Power 41", "WHEA-Logger 19").</param>
/// <param name="Detail">A short excerpt of what it said, never a whole log.</param>
public record HealthEventInput(DateTimeOffset OccurredUtc, string Kind, string Source, string? Detail);

/// <summary>
/// What an agent read from its operating system's own log about the device shutting down by itself and
/// about hardware errors: read-only, and only since the last report. Sent by the agent with its client
/// certificate (<c>POST /api/host-health-events</c>), never by a browser.
/// </summary>
public record ReportHostHealthEventsCommand(string SerialNumber, IReadOnlyList<HealthEventInput> Events)
    : IRequest<int>, IAgentScopedRequest;
