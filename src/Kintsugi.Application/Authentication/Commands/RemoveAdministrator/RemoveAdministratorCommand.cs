using MediatR;

namespace Kintsugi.Application.Authentication.Commands.RemoveAdministrator;

/// <summary>Takes one address off the administrators list. Removing one that is not there is not an
/// error: the list is as the caller wanted it.</summary>
public record RemoveAdministratorCommand(string Email) : IRequest<IReadOnlyList<AdministratorDto>>;
