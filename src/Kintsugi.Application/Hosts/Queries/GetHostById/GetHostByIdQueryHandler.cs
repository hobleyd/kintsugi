using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Hosts.Queries.GetHostById;

public class GetHostByIdQueryHandler : IRequestHandler<GetHostByIdQuery, HostDto?>
{
    private readonly IHostRepository _hostRepository;

    public GetHostByIdQueryHandler(IHostRepository hostRepository)
    {
        _hostRepository = hostRepository;
    }

    public async Task<HostDto?> Handle(GetHostByIdQuery request, CancellationToken cancellationToken)
    {
        var host = await _hostRepository.GetByIdAsync(request.Id, cancellationToken);
        return host is null ? null : HostDto.FromEntity(host);
    }
}
