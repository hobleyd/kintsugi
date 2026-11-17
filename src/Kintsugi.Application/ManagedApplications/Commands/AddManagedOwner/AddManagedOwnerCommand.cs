using MediatR;

namespace Kintsugi.Application.ManagedApplications.Commands.AddManagedOwner;

/// <summary>Adds a tool to the list that can own a managed application. Adding one already there is
/// not an error.</summary>
public record AddManagedOwnerCommand(string Name) : IRequest<ManagedApplicationsDto>;
