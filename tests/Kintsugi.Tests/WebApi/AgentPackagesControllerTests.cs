using Kintsugi.WebApi.Controllers;

namespace Kintsugi.Tests.WebApi;

public class AgentPackagesControllerTests
{
    [Fact]
    public void RequestPresentedAVerifiedAgentCertificate_ExactSuccess_ReturnsTrue()
    {
        Assert.True(AgentPackagesController.RequestPresentedAVerifiedAgentCertificate("SUCCESS"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NONE")]
    [InlineData("FAILED:self signed certificate")]
    [InlineData("success")]
    public void RequestPresentedAVerifiedAgentCertificate_AnythingElse_ReturnsFalse(string? headerValue)
    {
        Assert.False(AgentPackagesController.RequestPresentedAVerifiedAgentCertificate(headerValue));
    }
}
