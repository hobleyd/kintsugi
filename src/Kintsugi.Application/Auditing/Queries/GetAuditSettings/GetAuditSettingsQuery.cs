using MediatR;

namespace Kintsugi.Application.Auditing.Queries.GetAuditSettings;

public record GetAuditSettingsQuery : IRequest<AuditSettingsDto>;
