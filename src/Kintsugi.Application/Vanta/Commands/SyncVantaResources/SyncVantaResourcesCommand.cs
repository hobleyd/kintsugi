using MediatR;

namespace Kintsugi.Application.Vanta.Commands.SyncVantaResources;

/// <summary>
/// Sends this fleet's current patch state to Vanta as a complete state-of-the-world replacement.
/// Dispatched by the background sync on its timer and by "Sync now" on the settings screen; both go
/// through <c>VantaSyncCoordinator</c>, which allows only one run at a time.
/// </summary>
public record SyncVantaResourcesCommand : IRequest<VantaSyncResultDto>;
