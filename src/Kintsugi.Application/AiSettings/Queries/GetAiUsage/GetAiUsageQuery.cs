using MediatR;

namespace Kintsugi.Application.AiSettings.Queries.GetAiUsage;

public record GetAiUsageQuery : IRequest<AiUsageDto>;
