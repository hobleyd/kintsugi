using FluentValidation;
using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.HostActivity;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.LogCollections.Commands;

/// <param name="Detail">"WarningsAndErrors" or "Everything".</param>
/// <param name="RequestedBy">The signed-in administrator, set by the controller and never read from the body.</param>
public record RequestLogCollectionCommand(Guid HostId, int PeriodDays, string Detail, string RequestedBy) : IRequest<LogCollectionDto>;

public class RequestLogCollectionCommandValidator : AbstractValidator<RequestLogCollectionCommand>
{
    public RequestLogCollectionCommandValidator()
    {
        RuleFor(x => x.PeriodDays).Must(d => LogCollection.AllowedPeriodDays.Contains(d)).WithMessage("Choose 7 or 30 days.");
        RuleFor(x => x.Detail).Must(d => Enum.TryParse<LogCollectionDetail>(d, out var detail) && Enum.IsDefined(detail)).WithMessage("Choose warnings and errors, or everything.");
        RuleFor(x => x.RequestedBy).NotEmpty();
    }
}

public class RequestLogCollectionCommandHandler : IRequestHandler<RequestLogCollectionCommand, LogCollectionDto>
{
    private readonly IHostRepository _hosts;
    private readonly ILogCollectionRepository _collections;
    private readonly IHostEventRecorder _events;
    private readonly IUnitOfWork _unitOfWork;

    public RequestLogCollectionCommandHandler(IHostRepository hosts, ILogCollectionRepository collections, IHostEventRecorder events, IUnitOfWork unitOfWork)
    {
        _hosts = hosts;
        _collections = collections;
        _events = events;
        _unitOfWork = unitOfWork;
    }

    public async Task<LogCollectionDto> Handle(RequestLogCollectionCommand request, CancellationToken cancellationToken)
    {
        var host = await _hosts.GetByIdAsync(request.HostId, cancellationToken)
            ?? throw new NotFoundException($"No device with id '{request.HostId}'.");

        var now = DateTimeOffset.UtcNow;
        var open = await _collections.GetOpenForHostAsync(host.Id, cancellationToken);
        if (open is not null && open.IsActiveAt(now))
        {
            throw new ConflictException("Logs have already been requested from this device and it has not answered yet. Cancel that request first.");
        }

        var detail = Enum.Parse<LogCollectionDetail>(request.Detail);
        var collection = LogCollection.Request(host.Id, request.RequestedBy, request.PeriodDays, detail, now);
        await _collections.AddAsync(collection, cancellationToken);
        await _events.LogsRequestedAsync(host.Id, request.RequestedBy, $"Last {request.PeriodDays} days, {(detail == LogCollectionDetail.Everything ? "everything" : "warnings and errors")}", now, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return LogCollectionSummary.From(collection).ToDto(now);
    }
}
