using System.Security.Cryptography;
using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Hosts.Commands.EnrollAgent;

namespace Kintsugi.Tests.Security;

public class EnrollAgentCommandHandlerTests
{
    private readonly Mock<ICaService> _caService = new();
    private readonly Mock<IArtifactSigningService> _artifactSigningService = new();
    private readonly Mock<IAgentEnrollmentOptions> _enrollmentOptions = new();

    private EnrollAgentCommandHandler CreateHandler() =>
        new(_caService.Object, _artifactSigningService.Object, _enrollmentOptions.Object);

    [Fact]
    public async Task Handle_WithTheCorrectToken_IssuesACertificateForTheClaimedSerialNumber()
    {
        _enrollmentOptions.Setup(o => o.EnrollmentToken).Returns("correct-token");
        _caService.Setup(s => s.IssueClientCertificatePem("csr-pem", "SERIAL-1", It.IsAny<TimeSpan>())).Returns("cert-pem");
        _caService.Setup(s => s.GetCaCertificatePem()).Returns("ca-pem");
        _artifactSigningService.Setup(s => s.GetPublicKeyPem()).Returns("artifact-pubkey-pem");

        var result = await CreateHandler().Handle(new EnrollAgentCommand("SERIAL-1", "correct-token", "csr-pem"), CancellationToken.None);

        Assert.Equal("cert-pem", result.CertificatePem);
        Assert.Equal("ca-pem", result.CaCertificatePem);
        Assert.Equal("artifact-pubkey-pem", result.ArtifactSigningPublicKeyPem);
        _caService.Verify(s => s.IssueClientCertificatePem("csr-pem", "SERIAL-1", It.IsAny<TimeSpan>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithTheWrongToken_ThrowsForbiddenAndNeverTouchesTheCa()
    {
        _enrollmentOptions.Setup(o => o.EnrollmentToken).Returns("correct-token");

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            CreateHandler().Handle(new EnrollAgentCommand("SERIAL-1", "wrong-token", "csr-pem"), CancellationToken.None));

        _caService.Verify(s => s.IssueClientCertificatePem(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Handle_WithNoTokenConfiguredServerSide_ThrowsForbidden_RegardlessOfWhatWasSubmitted(string? configuredToken)
    {
        _enrollmentOptions.Setup(o => o.EnrollmentToken).Returns(configuredToken);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            CreateHandler().Handle(new EnrollAgentCommand("SERIAL-1", "anything", "csr-pem"), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_WhenCaServiceRejectsTheCsr_WrapsItAsForbidden_NotAnUnhandled500()
    {
        _enrollmentOptions.Setup(o => o.EnrollmentToken).Returns("correct-token");
        _caService
            .Setup(s => s.IssueClientCertificatePem(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Throws(new CryptographicException("bad signature"));

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            CreateHandler().Handle(new EnrollAgentCommand("SERIAL-1", "correct-token", "malformed-csr"), CancellationToken.None));
    }
}
