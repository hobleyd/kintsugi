using Microsoft.Extensions.Configuration;
using Kintsugi.Infrastructure.Security;

namespace Kintsugi.Tests.Infrastructure;

public class AgentApiOptionsTests
{
    private static AgentApiOptions WithValue(string? value) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AGENT_API_BASE_URL"] = value })
            .Build());

    [Fact]
    public void AgentApiBaseUrl_ReturnsTheConfiguredValue() =>
        Assert.Equal("https://ishtar.example.com:8443", WithValue("https://ishtar.example.com:8443").AgentApiBaseUrl);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AgentApiBaseUrl_UnsetOrBlank_IsNull_SoTheCallerKnowsToFallBackAndSaySo(string? value) =>
        Assert.Null(WithValue(value).AgentApiBaseUrl);

    [Fact]
    public void AgentApiBaseUrl_TrimsATrailingSlash()
    {
        // The agent joins this with paths that already begin with one. A doubled slash changes the
        // request path nginx matches its exact-match agent regex against, which would 403 every
        // agent route for a reason nothing on the page would explain.
        Assert.Equal("https://ishtar.example.com:8443", WithValue("https://ishtar.example.com:8443/").AgentApiBaseUrl);
    }

    [Fact]
    public void AgentApiBaseUrl_TrimsSurroundingWhitespace() =>
        Assert.Equal("https://ishtar.example.com:8443", WithValue("  https://ishtar.example.com:8443  ").AgentApiBaseUrl);
}
