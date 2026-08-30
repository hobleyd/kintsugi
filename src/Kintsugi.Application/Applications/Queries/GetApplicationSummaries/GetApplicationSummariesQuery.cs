using MediatR;

namespace Kintsugi.Application.Applications.Queries.GetApplicationSummaries;

public record GetApplicationSummariesQuery : IRequest<IReadOnlyList<ApplicationSummaryDto>>;
