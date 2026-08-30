using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AiSettings.Queries.GetGooseCliStatus;

public class GetGooseCliStatusQueryHandler : IRequestHandler<GetGooseCliStatusQuery, GooseCliStatus>
{
    private readonly IGooseCliClient _client;

    public GetGooseCliStatusQueryHandler(IGooseCliClient client)
    {
        _client = client;
    }

    public Task<GooseCliStatus> Handle(GetGooseCliStatusQuery request, CancellationToken cancellationToken) =>
        _client.CheckAvailabilityAsync(request.Endpoint, cancellationToken);
}
