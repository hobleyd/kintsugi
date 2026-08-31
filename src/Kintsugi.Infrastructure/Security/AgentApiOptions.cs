using Microsoft.Extensions.Configuration;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.Security;

public class AgentApiOptions : IAgentApiOptions
{
    private readonly IConfiguration _configuration;

    public AgentApiOptions(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>Trailing slash trimmed here rather than at each use: the agent joins this with
    /// paths that already begin with one, and a doubled slash changes the request path nginx
    /// matches its exact-match agent regex against — which would 403 every agent route for a
    /// reason nothing on the page would explain.</summary>
    public string? AgentApiBaseUrl
    {
        get
        {
            var configured = _configuration["AGENT_API_BASE_URL"];
            return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim().TrimEnd('/');
        }
    }
}
