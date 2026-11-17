using MediatR;

namespace Kintsugi.Application.ManagedApplications.Commands.RemoveManagedOwner;

/// <summary>Takes a tool off the list. Refused while an application is still managed by it: removing
/// the name would leave those applications owned by something the list does not show.</summary>
public record RemoveManagedOwnerCommand(string Name) : IRequest<ManagedApplicationsDto>;
