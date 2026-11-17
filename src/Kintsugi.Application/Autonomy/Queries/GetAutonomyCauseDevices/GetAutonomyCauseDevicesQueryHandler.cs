using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Autonomy.Queries.GetAutonomyCauseDevices;

public class GetAutonomyCauseDevicesQueryHandler : IRequestHandler<GetAutonomyCauseDevicesQuery, AutonomyDevicesPageDto>
{
    private readonly IHostRepository _hosts;
    private readonly IPatchFailureRepository _failures;
    private readonly IUpgradePathRepository _upgradePaths;

    public GetAutonomyCauseDevicesQueryHandler(IHostRepository hosts, IPatchFailureRepository failures, IUpgradePathRepository upgradePaths)
    {
        _hosts = hosts;
        _failures = failures;
        _upgradePaths = upgradePaths;
    }

    public async Task<AutonomyDevicesPageDto> Handle(GetAutonomyCauseDevicesQuery request, CancellationToken cancellationToken)
    {
        var inputs = await AutonomyInputs.LoadAsync(_hosts, _failures, _upgradePaths, cancellationToken);
        var cause = inputs.Causes(DateTimeOffset.UtcNow).FirstOrDefault(c => c.Key == request.Key)
            ?? throw new NotFoundException($"No cause '{request.Key}' is holding patching up.");

        var matching = string.IsNullOrWhiteSpace(request.Search)
            ? cause.Devices
            : cause.Devices.Where(d => d.Hostname.Contains(request.Search.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

        return new AutonomyDevicesPageDto(
            matching.Count,
            matching.Skip(request.Skip).Take(request.Take)
                .Select(d => new AutonomyDeviceDto(d.HostId, d.Hostname, d.Detail, d.Connected, d.PatchNowEligible))
                .ToList());
    }
}
