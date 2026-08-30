using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Hosts.Commands.CreateHost;

public class CreateHostCommandHandler : IRequestHandler<CreateHostCommand, CreateHostResult>
{
    private readonly IHostRepository _hostRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICheckInLoadBalancer _loadBalancer;

    public CreateHostCommandHandler(IHostRepository hostRepository, IUnitOfWork unitOfWork, ICheckInLoadBalancer loadBalancer)
    {
        _hostRepository = hostRepository;
        _unitOfWork = unitOfWork;
        _loadBalancer = loadBalancer;
    }

    public async Task<CreateHostResult> Handle(CreateHostCommand request, CancellationToken cancellationToken)
    {
        var suggestedCheckInMinute = _loadBalancer.RecordCheckIn(request.SerialNumber, request.CheckInMinute);

        var existing = await _hostRepository.GetBySerialNumberAsync(request.SerialNumber, cancellationToken);

        if (existing is not null)
        {
            existing.Reregister(
                request.Hostname,
                request.OperatingSystem,
                request.IpAddress,
                request.OperatingSystemUpdateAvailable,
                request.OperatingSystemLatestVersion);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return new CreateHostResult(HostDto.FromEntity(existing), WasCreated: false, suggestedCheckInMinute);
        }

        var host = new Host(
            request.Hostname,
            request.SerialNumber,
            request.OperatingSystem,
            request.IpAddress,
            request.OperatingSystemUpdateAvailable,
            request.OperatingSystemLatestVersion);
        host.RecordHeartbeat(HostStatus.Online);

        await _hostRepository.AddAsync(host, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new CreateHostResult(HostDto.FromEntity(host), WasCreated: true, suggestedCheckInMinute);
    }
}
