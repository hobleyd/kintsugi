using MediatR;

namespace Kintsugi.Application.Autonomy.Queries.GetAutonomyCauseDevices;

/// <summary>A page of the devices held up by one cause, so a fleet of any size is read a page at a time.</summary>
public record GetAutonomyCauseDevicesQuery(string Key, int Skip = 0, int Take = 25, string? Search = null)
    : IRequest<AutonomyDevicesPageDto>;
