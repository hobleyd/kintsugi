using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AiSettings.Commands.TestAiConnection;

/// <summary>Sends one tiny prompt down a stored connection — proves the URL, the credential and the
/// model name before a scan finds out the hard way.</summary>
public record TestAiConnectionCommand(Guid ConnectionId, string Model) : IRequest<AiConnectionTestResultDto>;

public class TestAiConnectionCommandHandler : IRequestHandler<TestAiConnectionCommand, AiConnectionTestResultDto>
{
    private readonly IAiConnectionRepository _connections;
    private readonly IAiConnectionProbe _probe;

    public TestAiConnectionCommandHandler(IAiConnectionRepository connections, IAiConnectionProbe probe)
    {
        _connections = connections;
        _probe = probe;
    }

    public async Task<AiConnectionTestResultDto> Handle(TestAiConnectionCommand request, CancellationToken cancellationToken)
    {
        var connection = await _connections.GetAsync(request.ConnectionId, cancellationToken)
            ?? throw new NotFoundException($"AI connection {request.ConnectionId} was not found.");

        return await _probe.ProbeAsync(ToSettings(connection), request.Model, cancellationToken);
    }

    private static AiConnectionSettings ToSettings(Domain.Entities.AiConnection c) => new(
        c.Name, c.Protocol, c.BaseUrl, c.AuthMode, c.ApiKey, c.GoogleCloudProject, c.GoogleCloudLocation, c.UseHostedWebSearch);
}
