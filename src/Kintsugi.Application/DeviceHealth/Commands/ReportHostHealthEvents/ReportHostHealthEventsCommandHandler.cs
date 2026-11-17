using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.DeviceHealth.Commands.ReportHostHealthEvents;

public class ReportHostHealthEventsCommandHandler : IRequestHandler<ReportHostHealthEventsCommand, int>
{
    /// <summary>Two reports of the same kind this close together are one event: the operating system often
    /// logs a crash twice (a bug check and then "rebooted without a clean shutdown"), and a retried report
    /// must not double what it already sent.</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(2);

    /// <summary>The most health events kept for one host in 30 days. Past it nothing more is stored, but the
    /// host is already flagged as sending an unusual amount (see <see cref="HostHealthAssessor"/>), so the
    /// flood is seen and the table cannot be filled by one host.</summary>
    public const int MaxStoredPer30Days = 500;

    private readonly IHostRepository _hosts;
    private readonly IHostEventRepository _events;
    private readonly IUnitOfWork _unitOfWork;

    public ReportHostHealthEventsCommandHandler(IHostRepository hosts, IHostEventRepository events, IUnitOfWork unitOfWork)
    {
        _hosts = hosts;
        _events = events;
        _unitOfWork = unitOfWork;
    }

    /// <returns>How many events were recorded: those not already known.</returns>
    public async Task<int> Handle(ReportHostHealthEventsCommand request, CancellationToken cancellationToken)
    {
        var host = await _hosts.GetBySerialNumberAsync(request.SerialNumber, cancellationToken)
            ?? throw new NotFoundException($"No host is registered with serial number '{request.SerialNumber}'.");

        if (request.Events.Count == 0)
        {
            return 0;
        }

        var monthAgo = DateTimeOffset.UtcNow.AddDays(-30);
        var since = new[] { request.Events.Min(e => e.OccurredUtc).Add(-DuplicateWindow), monthAgo }.Min();
        var known = (await _events.GetHealthEventsForHostSinceAsync(host.Id, since, cancellationToken)).ToList();
        var room = Math.Max(0, MaxStoredPer30Days - known.Count(k => k.OccurredUtc >= monthAgo));

        var recorded = 0;
        foreach (var e in request.Events.OrderBy(e => e.OccurredUtc))
        {
            if (recorded >= room)
            {
                break;
            }

            var kind = Enum.Parse<HostEventKind>(e.Kind);
            if (known.Any(k => k.Kind == kind && (k.OccurredUtc - e.OccurredUtc).Duration() <= DuplicateWindow))
            {
                continue;
            }

            await _events.AddAsync(HostEvent.Record(host.Id, kind, e.Source, e.OccurredUtc, detail: e.Detail), cancellationToken);
            known.Add(new HealthEventRecord(host.Id, kind, e.OccurredUtc));
            recorded++;
        }

        if (recorded > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return recorded;
    }
}
