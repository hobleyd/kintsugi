using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Auditing.Queries.GetAuditSettings;

public class GetAuditSettingsQueryHandler : IRequestHandler<GetAuditSettingsQuery, AuditSettingsDto>
{
    private readonly IAuditSettingsRepository _repository;

    public GetAuditSettingsQueryHandler(IAuditSettingsRepository repository)
    {
        _repository = repository;
    }

    public async Task<AuditSettingsDto> Handle(GetAuditSettingsQuery request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);
        return settings is null ? AuditSettingsDto.NotConfigured() : AuditSettingsDto.FromEntity(settings);
    }
}
