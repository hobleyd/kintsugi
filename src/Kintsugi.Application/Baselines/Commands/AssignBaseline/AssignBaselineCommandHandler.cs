using FluentValidation;
using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.Baselines.Commands.AssignBaseline;

public class AssignBaselineCommandHandler : IRequestHandler<AssignBaselineCommand>
{
    private readonly IBaselineRepository _baselines;
    private readonly IHostRepository _hosts;
    private readonly IUnitOfWork _unitOfWork;

    public AssignBaselineCommandHandler(IBaselineRepository baselines, IHostRepository hosts, IUnitOfWork unitOfWork)
    {
        _baselines = baselines;
        _hosts = hosts;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(AssignBaselineCommand request, CancellationToken cancellationToken)
    {
        var baseline = await _baselines.GetAsync(request.BaselineId, cancellationToken)
            ?? throw new NotFoundException($"No baseline with id '{request.BaselineId}'.");

        var hostIds = new List<Guid>();
        var unknown = new List<string>();
        var notWindows = new List<string>();
        foreach (var name in request.HostNames.Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var host = await _hosts.GetByHostnameAsync(name, cancellationToken);
            if (host is null || host.DeletedAtUtc is not null)
            {
                unknown.Add(name);
            }
            // Baselines hold registry items, and only the Windows agent fetches them (baseline.rs). A Mac or
            // Linux host named here would never report, and would sit in every count as not reported.
            else if (!BaselineCompliance.IsWindows(host))
            {
                notWindows.Add($"{name} ({host.OperatingSystem ?? "unknown OS"})");
            }
            else
            {
                hostIds.Add(host.Id);
            }
        }

        // All or nothing: assigning to "most of" a list someone typed would quietly leave a test device out.
        var problems = new List<string>();
        if (unknown.Count > 0)
        {
            problems.Add("No host is registered as: " + string.Join(", ", unknown) + ".");
        }

        if (notWindows.Count > 0)
        {
            problems.Add("Baselines apply to Windows hosts only, and these are not: " + string.Join(", ", notWindows) + ".");
        }

        if (problems.Count > 0)
        {
            throw new DomainException(string.Join(" ", problems) + " Nothing was assigned.");
        }

        baseline.Assign(request.AllWindowsHosts, hostIds, request.AssignedBy);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
