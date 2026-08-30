using MediatR;

namespace Kintsugi.Application.Authentication.Queries.GetAuthenticationSettings;

public record GetAuthenticationSettingsQuery : IRequest<AuthenticationSettingsDto>;
