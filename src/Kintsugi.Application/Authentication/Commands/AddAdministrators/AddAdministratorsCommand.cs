using MediatR;

namespace Kintsugi.Application.Authentication.Commands.AddAdministrators;

/// <summary>Adds addresses to the administrators list. <paramref name="AddedBy"/> is not part of the
/// request body: the controller fills it from the signed-in caller, so a client cannot claim to be
/// somebody else.</summary>
public record AddAdministratorsCommand(IReadOnlyList<string> Emails, string? AddedBy = null) : IRequest<IReadOnlyList<AdministratorDto>>;
