namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// The agent fleet's own private certificate authority — distinct from the server's own TLS
/// certificate. Issues each enrolled agent a client certificate (see
/// <c>EnrollAgentCommandHandler</c>) that nginx then requires, and verifies, on every subsequent
/// agent-only request (see nginx/default.conf). The CA keypair is generated once and persisted;
/// see the infrastructure implementation for where.
/// </summary>
public interface ICaService
{
    /// <summary>The CA's own certificate (public only), PEM-encoded — handed to a newly enrolled
    /// agent so it can pin/trust it independently of ordinary system root CAs.</summary>
    string GetCaCertificatePem();

    /// <summary>
    /// Issues a client certificate binding <paramref name="commonName"/> (always the host's own
    /// serial number — see <c>EnrollAgentCommandHandler</c>) to the public key proven, by the CSR's
    /// own signature, to be held by whoever generated it. Deliberately ignores every other field
    /// the CSR itself requests (subject, extensions, ...) — only the proven public key is trusted
    /// from caller-supplied input; the identity it's bound to comes from <paramref
    /// name="commonName"/> alone, which the caller must already have authenticated some other way
    /// (the enrollment token).
    /// </summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The CSR is malformed
    /// or its self-signature doesn't verify.</exception>
    string IssueClientCertificatePem(string csrPem, string commonName, TimeSpan validity);
}
