using System.Security.Cryptography;
using System.Text;
using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Hosts.Commands.EnrollAgent;

public class EnrollAgentCommandHandler : IRequestHandler<EnrollAgentCommand, EnrollAgentResult>
{
    /// <summary>
    /// Long enough that a healthy agent (which re-enrolls on its own well before this — see the
    /// kintsugi-agent's own renewal check) never gets cut off mid-lifetime by surprise, short
    /// enough that a certificate stolen off a decommissioned host doesn't stay usable indefinitely.
    /// </summary>
    private static readonly TimeSpan CertificateValidity = TimeSpan.FromDays(397);

    private readonly ICaService _caService;
    private readonly IArtifactSigningService _artifactSigningService;
    private readonly IAgentEnrollmentOptions _enrollmentOptions;

    public EnrollAgentCommandHandler(ICaService caService, IArtifactSigningService artifactSigningService, IAgentEnrollmentOptions enrollmentOptions)
    {
        _caService = caService;
        _artifactSigningService = artifactSigningService;
        _enrollmentOptions = enrollmentOptions;
    }

    public Task<EnrollAgentResult> Handle(EnrollAgentCommand request, CancellationToken cancellationToken)
    {
        var expectedToken = _enrollmentOptions.EnrollmentToken;
        if (string.IsNullOrEmpty(expectedToken) || !ConstantTimeEquals(expectedToken, request.EnrollmentToken))
        {
            throw new ForbiddenException("Enrollment token is missing or incorrect.");
        }

        string certificatePem;
        try
        {
            certificatePem = _caService.IssueClientCertificatePem(request.CsrPem, request.SerialNumber, CertificateValidity);
        }
        catch (CryptographicException ex)
        {
            throw new ForbiddenException($"Could not issue a certificate from the supplied CSR: {ex.Message}");
        }

        return Task.FromResult(new EnrollAgentResult(certificatePem, _caService.GetCaCertificatePem(), _artifactSigningService.GetPublicKeyPem()));
    }

    /// <summary>Ordinary <c>==</c> on the token would short-circuit on the first mismatched byte,
    /// letting a timing attack narrow it down character by character — not a realistic risk for a
    /// long random token over a network with jitter, but there's no reason not to close it off.</summary>
    private static bool ConstantTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
