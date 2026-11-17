using MediatR;

namespace Kintsugi.Application.ManagedApplications.Queries.GetManagedApplications;

public record GetManagedApplicationsQuery : IRequest<ManagedApplicationsDto>;
