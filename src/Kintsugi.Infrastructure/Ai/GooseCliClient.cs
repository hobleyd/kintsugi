using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.Ai;

/// <summary>
/// Talks to a Goose agent over the Agent Client Protocol (see <see cref="GooseAcpConnection"/>)
/// rather than shelling out to a local <c>goose</c> installation — this system runs in a Docker
/// container with no Goose of its own. It connects to a <c>goose serve</c> instance's WebSocket
/// endpoint — not its HTTP+SSE endpoint, which accepts requests and creates sessions but never
/// actually dispatches <c>session/prompt</c> to the agent loop in the version this was verified
/// against (confirmed by comparison against Goose Desktop, which uses WebSocket and works
/// correctly against the same server) — reachable over the network from this container,
/// authenticating with the optional GOOSE_SERVE_SECRET_KEY configuration value when the target
/// requires it. <paramref name="endpoint"/> parameters below hold that instance's base URL (e.g.
/// "http://100.x.x.x:3284" — same http(s) scheme as the REST API despite connecting over
/// WebSocket, translated internally); blank defaults to Goose's own default local address, for the
/// case where the API happens to run alongside a `goose serve` instance directly.
/// </summary>
public class GooseCliClient : IGooseCliClient
{
    private const string DefaultBaseUrl = "http://127.0.0.1:3284";

    // A real research turn over ACP can involve extended "thinking" plus several tool-call
    // round-trips — observed continuously streaming (not stalled) for 270+s with a local 27B
    // model before finishing. Runs as a background job (see UpgradePathRefreshCoordinator), so
    // there's no user-facing cost to a generous ceiling here.
    // The merged single-call script-generation prompt asks the model to research the application
    // AND author a complete, validated two-mode script in one turn — observed to still be making
    // real progress (editing the script file via its own tool calls) right up against the old
    // 900s budget, so this needs more headroom than the earlier, more narrowly-scoped prompts did.
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(1500);
    private static readonly TimeSpan StatusCheckTimeout = TimeSpan.FromSeconds(10);

    private readonly IConfiguration _configuration;
    private readonly ILogger<GooseCliClient> _logger;

    public GooseCliClient(IConfiguration configuration, ILogger<GooseCliClient> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<GooseCliStatus> CheckAvailabilityAsync(string? endpoint, CancellationToken cancellationToken)
    {
        var baseUri = ResolveBaseUri(endpoint);

        using var timeoutCts = new CancellationTokenSource(StatusCheckTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await using var connection = await GooseAcpConnection.ConnectAsync(baseUri, SecretKey, _logger, linkedCts.Token);
            var (name, version) = await connection.InitializeAsync(linkedCts.Token);

            var label = string.IsNullOrWhiteSpace(name) ? version : $"{name} {version}".Trim();
            return new GooseCliStatus(IsAvailable: true, Version: string.IsNullOrWhiteSpace(label) ? null : label, Error: null);
        }
        catch (ExternalServiceException ex)
        {
            return new GooseCliStatus(IsAvailable: false, Version: null, Error: ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GooseCliStatus(IsAvailable: false, Version: null, Error: $"Timed out connecting to the Goose serve endpoint at {baseUri}.");
        }
    }

    public async Task<string> RunAsync(string prompt, string? model, string? endpoint, CancellationToken cancellationToken)
    {
        var baseUri = ResolveBaseUri(endpoint);

        using var timeoutCts = new CancellationTokenSource(RunTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        string response;
        try
        {
            await using var connection = await GooseAcpConnection.ConnectAsync(baseUri, SecretKey, _logger, linkedCts.Token);
            await connection.InitializeAsync(linkedCts.Token);
            var sessionId = await connection.CreateSessionAsync(model, linkedCts.Token);
            response = await connection.PromptAsync(sessionId, prompt, linkedCts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Goose run against {BaseUri} timed out after {Timeout}", baseUri, RunTimeout);
            throw new ExternalServiceException($"Timed out waiting for the Goose agent to respond after {RunTimeout.TotalMinutes:0} minutes.", ex);
        }

        if (string.IsNullOrWhiteSpace(response))
        {
            throw new ExternalServiceException("The Goose agent returned an empty response.");
        }

        return response;
    }

    private string? SecretKey => _configuration["GOOSE_SERVE_SECRET_KEY"];

    private static Uri ResolveBaseUri(string? endpoint)
    {
        var value = string.IsNullOrWhiteSpace(endpoint) ? DefaultBaseUrl : endpoint.Trim();

        if (!value.EndsWith('/'))
        {
            value += "/";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ExternalServiceException($"'{endpoint}' is not a valid Goose serve URL.");
        }

        return uri;
    }
}
