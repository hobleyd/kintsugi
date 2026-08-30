using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Hosts.Queries.GetHosts;

public class GetHostsQueryHandler : IRequestHandler<GetHostsQuery, IReadOnlyList<HostDto>>
{
    private readonly IHostRepository _hostRepository;
    private readonly IUpgradePathRepository _upgradePathRepository;

    public GetHostsQueryHandler(IHostRepository hostRepository, IUpgradePathRepository upgradePathRepository)
    {
        _hostRepository = hostRepository;
        _upgradePathRepository = upgradePathRepository;
    }

    public async Task<IReadOnlyList<HostDto>> Handle(GetHostsQuery request, CancellationToken cancellationToken)
    {
        var hosts = await _hostRepository.GetAllAsync(cancellationToken);
        var appUpdateCounts = await _upgradePathRepository.GetAppUpdateCountsByHostAsync(cancellationToken);
        return hosts
            .Select(host => HostDto.FromEntity(host, appUpdateCounts.GetValueOrDefault(host.Id)))
            .ToList();
    }
}
