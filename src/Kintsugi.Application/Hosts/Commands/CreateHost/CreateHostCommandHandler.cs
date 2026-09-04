using MediatR;
using Kintsugi.Application.Common.Exceptions;
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
            // Reregister can *change* the hostname — a renamed machine keeps its serial number — so
            // this branch reaches the same unique index the insert below does.
            await ReclaimHostnameAsync(request.Hostname, existing.Id, cancellationToken);

            existing.Reregister(
                request.Hostname,
                request.OperatingSystem,
                request.IpAddress,
                request.OperatingSystemUpdateAvailable,
                request.OperatingSystemLatestVersion,
                request.AgentVersion);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return new CreateHostResult(HostDto.FromEntity(existing), WasCreated: false, suggestedCheckInMinute);
        }

        await ReclaimHostnameAsync(request.Hostname, selfId: null, cancellationToken);

        var host = new Host(
            request.Hostname,
            request.SerialNumber,
            request.OperatingSystem,
            request.IpAddress,
            request.OperatingSystemUpdateAvailable,
            request.OperatingSystemLatestVersion,
            request.AgentVersion);
        host.RecordHeartbeat(HostStatus.Online);

        await _hostRepository.AddAsync(host, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new CreateHostResult(HostDto.FromEntity(host), WasCreated: true, suggestedCheckInMinute);
    }

    /// <summary>
    /// Frees the hostname before inserting under it, for the one case where a machine comes back
    /// with a different identity than the one it left with: an admin removed the host, and it is
    /// now re-registering under a serial number no row holds.
    ///
    /// That happens more readily than it looks. Removal is two-phase — RequestHostRemoval only
    /// soft-deletes (<see cref="Host.DeletedAtUtc"/>), and the row is not actually deleted until the
    /// agent confirms it uninstalled itself, which an agent that cannot authenticate never does. So
    /// the row lingers, invisible on the hosts list but still owning its name in a unique index. Add
    /// a re-imaged machine, or a Windows/Linux host whose serial moved between rungs of its
    /// fallback chain (see the Windows agent's choose_serial_number), and the insert below collides
    /// with a row nobody can see. Unhandled, that is a 500 on a route agents call unattended every
    /// hour, and the only clue is a constraint name in the server log.
    ///
    /// Deliberately narrow. It fires only when no row matched the reported serial number, so the
    /// ordinary removal flow is untouched: a host re-registering under the *same* serial still finds
    /// its own row above, still carries RemovalRequested, and is still told to uninstall itself
    /// rather than being quietly resurrected here. And a hostname held by a host that is *not*
    /// removed is a genuine collision between two live machines — two hosts really do claim one
    /// name, and silently deleting either one's record would be the wrong call to make on an agent's
    /// say-so, so it is reported instead.
    /// </summary>
    private async Task ReclaimHostnameAsync(string hostname, Guid? selfId, CancellationToken cancellationToken)
    {
        var holder = await _hostRepository.GetByHostnameAsync(hostname, cancellationToken);
        if (holder is null || holder.Id == selfId)
        {
            return;
        }

        if (holder.DeletedAtUtc is null)
        {
            throw new ConflictException(
                $"Hostname '{hostname}' is already registered to a different host (serial number '{holder.SerialNumber}'). " +
                "Two machines cannot share a hostname; rename one, or remove the host that no longer exists.");
        }

        // A hard delete, which is what the soft-deleted row was always headed for. Installed
        // applications go with it: installed_applications cascades on HostId, so the removed host's
        // inventory does not outlive it (see InstalledApplicationConfiguration).
        await _hostRepository.DeleteAsync(holder, cancellationToken);
    }
}
