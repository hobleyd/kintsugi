using MediatR;

namespace Kintsugi.Application.Hosts.Commands.EnrollAgent;

/// <summary>
/// A brand-new agent's one-time bootstrap into the fleet's mutual-TLS identity system: presents
/// the shared <see cref="EnrollmentToken"/> plus a CSR it generated itself (its private key never
/// leaves the agent), and gets back a client certificate bound to its serial number. Every
/// subsequent request from that agent is authenticated by nginx via that certificate — see
/// nginx/default.conf and <c>RequireAgentIdentityAttribute</c>.
/// </summary>
public record EnrollAgentCommand(string SerialNumber, string EnrollmentToken, string CsrPem) : IRequest<EnrollAgentResult>;

public record EnrollAgentResult(string CertificatePem, string CaCertificatePem, string ArtifactSigningPublicKeyPem);
