using MediatR;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpdateCheckStatus;

/// <summary>Polled by the UI while a "Check for Updates" run is in progress, to show live progress.</summary>
public record GetUpdateCheckStatusQuery : IRequest<UpdateCheckStatusDto>;
