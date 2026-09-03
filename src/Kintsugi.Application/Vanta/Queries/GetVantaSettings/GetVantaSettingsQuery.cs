using MediatR;

namespace Kintsugi.Application.Vanta.Queries.GetVantaSettings;

public record GetVantaSettingsQuery : IRequest<VantaSettingsDto>;
