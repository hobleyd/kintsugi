using Microsoft.Extensions.Configuration;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.Security;

public class AgentEnrollmentOptions : IAgentEnrollmentOptions
{
    private readonly IConfiguration _configuration;

    public AgentEnrollmentOptions(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string? EnrollmentToken => _configuration["AGENT_ENROLLMENT_TOKEN"];
}
