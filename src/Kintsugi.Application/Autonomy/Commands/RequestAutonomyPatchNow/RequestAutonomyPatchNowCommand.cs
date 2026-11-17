using MediatR;

namespace Kintsugi.Application.Autonomy.Commands.RequestAutonomyPatchNow;

/// <summary>
/// Patch now for every Windows device in one cause, resolved on the server so a cause of a thousand
/// devices is one request and the browser never carries the list.
/// </summary>
/// <param name="ExpectedConnected">How many connected devices the administrator was shown. The list
/// moves while the dialog is open, so a different number is refused rather than acted on.</param>
/// <param name="IncludeOffline">Also queue the offline devices, which start when they check in.</param>
/// <param name="RequestedBy">The signed-in administrator, set by the controller from the session.</param>
public record RequestAutonomyPatchNowCommand(
    string Key,
    int ExpectedConnected,
    bool IncludeOffline,
    string? RequestedBy = null) : IRequest<AutonomyPatchNowResult>;
