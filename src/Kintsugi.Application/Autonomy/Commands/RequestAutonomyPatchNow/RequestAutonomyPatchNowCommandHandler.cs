using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ForcedPatchRuns.Commands.RequestForcedPatchRuns;

namespace Kintsugi.Application.Autonomy.Commands.RequestAutonomyPatchNow;

public class RequestAutonomyPatchNowCommandHandler : IRequestHandler<RequestAutonomyPatchNowCommand, AutonomyPatchNowResult>
{
    /// <summary>The name a failed Windows update is filed under, and the name the Windows agent takes as
    /// "install the Windows updates now". The same instruction the Patch Health button sends.</summary>
    public const string WindowsName = "Windows";

    private readonly IHostRepository _hosts;
    private readonly IPatchFailureRepository _failures;
    private readonly IUpgradePathRepository _upgradePaths;
    private readonly ISender _sender;

    public RequestAutonomyPatchNowCommandHandler(
        IHostRepository hosts,
        IPatchFailureRepository failures,
        IUpgradePathRepository upgradePaths,
        ISender sender)
    {
        _hosts = hosts;
        _failures = failures;
        _upgradePaths = upgradePaths;
        _sender = sender;
    }

    public async Task<AutonomyPatchNowResult> Handle(RequestAutonomyPatchNowCommand request, CancellationToken cancellationToken)
    {
        var inputs = await AutonomyInputs.LoadAsync(_hosts, _failures, _upgradePaths, cancellationToken);
        var cause = inputs.Causes(DateTimeOffset.UtcNow).FirstOrDefault(c => c.Key == request.Key)
            ?? throw new NotFoundException($"No cause '{request.Key}' is holding patching up.");

        var connected = cause.Devices.Where(d => d.PatchNowEligible && d.Connected).Select(d => d.Hostname).Distinct().ToList();
        var offline = cause.Devices.Where(d => d.PatchNowEligible && !d.Connected).Select(d => d.Hostname).Distinct().ToList();

        if (connected.Count != request.ExpectedConnected)
        {
            throw new ConflictException(
                $"The list changed while it was open: {connected.Count} device(s) can be patched now, not {request.ExpectedConnected}. Open the cause again.");
        }

        var names = request.IncludeOffline ? connected.Concat(offline).ToList() : connected;
        if (names.Count == 0)
        {
            return new AutonomyPatchNowResult(0, 0, []);
        }

        // The existing Patch now command does the work, so each device gets the same instruction, the same
        // 24-hour lifetime, and an event on its timeline with the administrator's name.
        var result = await _sender.Send(
            new RequestForcedPatchRunsCommand(WindowsName, WindowsName, names, request.RequestedBy),
            cancellationToken);

        return new AutonomyPatchNowResult(
            connected.Count,
            request.IncludeOffline ? offline.Count : 0,
            result.NotRequested);
    }
}
