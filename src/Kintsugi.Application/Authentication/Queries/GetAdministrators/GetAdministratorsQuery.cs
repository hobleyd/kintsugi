using MediatR;

namespace Kintsugi.Application.Authentication.Queries.GetAdministrators;

public record GetAdministratorsQuery : IRequest<IReadOnlyList<AdministratorDto>>;
