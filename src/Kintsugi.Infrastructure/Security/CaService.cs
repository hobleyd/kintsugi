using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.Security;

/// <summary>
/// Generates (once) and persists the agent fleet's own private CA, and issues client certificates
/// from it. The private key never leaves <c>CaService:PrivateDirectory</c> — mounted only into the
/// api service (see docker-compose.yml's agent-ca-private volume); the CA's public certificate is
/// mirrored into <c>CaService:PublicDirectory</c> (agent-ca-public), which nginx mounts read-only
/// to verify agent client certificates against.
/// </summary>
public class CaService : ICaService
{
    private const string CaCommonName = "Kintsugi Agent Fleet CA";

    /// <summary>Ten years: this is the root of trust for every agent certificate — rotating it
    /// means re-enrolling the whole fleet, so it's deliberately long-lived rather than something
    /// meant to be renewed casually like a leaf certificate.</summary>
    private static readonly TimeSpan CaValidity = TimeSpan.FromDays(365 * 10);

    private readonly string _privateDirectory;
    private readonly string _publicDirectory;
    private readonly ILogger<CaService> _logger;
    private readonly object _lock = new();
    private X509Certificate2? _caCertificate;

    public CaService(IConfiguration configuration, ILogger<CaService> logger)
    {
        _privateDirectory = configuration["CaService:PrivateDirectory"] ?? "/data/agent-ca-private";
        _publicDirectory = configuration["CaService:PublicDirectory"] ?? "/data/agent-ca-public";
        _logger = logger;
    }

    private string CaCertificatePath => Path.Combine(_privateDirectory, "ca.crt");
    private string CaPrivateKeyPath => Path.Combine(_privateDirectory, "ca.key");
    private string PublicCaCertificatePath => Path.Combine(_publicDirectory, "ca.crt");

    public string GetCaCertificatePem() => LoadOrCreateCa().ExportCertificatePem();

    public string IssueClientCertificatePem(string csrPem, string commonName, TimeSpan validity)
    {
        var caCertificate = LoadOrCreateCa();
        var caPrivateKey = caCertificate.GetECDsaPrivateKey()
            ?? throw new CryptographicException("The agent fleet CA has no usable private key.");

        // Verifies the CSR's own signature — i.e. that whoever submitted it actually holds the
        // private key for the public key it contains — and throws CryptographicException if not.
        var csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256);

        // Only the CSR's proven public key is trusted from caller-supplied input; the subject and
        // every extension below are always ours, never whatever the CSR itself asked for — see
        // ICaService.IssueClientCertificatePem.
        var request = new CertificateRequest(new X500DistinguishedName($"CN={commonName}"), csr.PublicKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") /* id-kp-clientAuth */ }, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5); // clock-skew slack
        var notAfter = notBefore.Add(validity);
        var serialNumber = RandomNumberGenerator.GetBytes(16);

        using var issued = request.Create(
            caCertificate.SubjectName,
            X509SignatureGenerator.CreateForECDsa(caPrivateKey),
            notBefore,
            notAfter,
            serialNumber);

        return issued.ExportCertificatePem();
    }

    private X509Certificate2 LoadOrCreateCa()
    {
        if (_caCertificate is not null)
        {
            return _caCertificate;
        }

        lock (_lock)
        {
            if (_caCertificate is not null)
            {
                return _caCertificate;
            }

            Directory.CreateDirectory(_privateDirectory);
            Directory.CreateDirectory(_publicDirectory);

            if (File.Exists(CaCertificatePath) && File.Exists(CaPrivateKeyPath))
            {
                _caCertificate = X509Certificate2.CreateFromPemFile(CaCertificatePath, CaPrivateKeyPath);
                EnsurePublicCopy(_caCertificate);
                return _caCertificate;
            }

            _logger.LogInformation("No agent fleet CA found under {PrivateDirectory} — generating a new one.", _privateDirectory);

            using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var caRequest = new CertificateRequest($"CN={CaCommonName}", caKey, HashAlgorithmName.SHA256);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
            caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, critical: true));
            caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, critical: false));

            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            using var newCaCertificate = caRequest.CreateSelfSigned(notBefore, notBefore.Add(CaValidity));

            File.WriteAllText(CaCertificatePath, newCaCertificate.ExportCertificatePem());
            File.WriteAllText(CaPrivateKeyPath, caKey.ExportPkcs8PrivateKeyPem());
            TryRestrictToOwnerOnly(CaPrivateKeyPath);

            // Re-load rather than keep newCaCertificate itself: identical behavior to the
            // load-from-disk path above, so there's exactly one code path that produces the
            // long-lived _caCertificate this service actually signs with.
            _caCertificate = X509Certificate2.CreateFromPemFile(CaCertificatePath, CaPrivateKeyPath);
            EnsurePublicCopy(_caCertificate);
            return _caCertificate;
        }
    }

    private void EnsurePublicCopy(X509Certificate2 caCertificate)
    {
        var pem = caCertificate.ExportCertificatePem();
        if (!File.Exists(PublicCaCertificatePath) || File.ReadAllText(PublicCaCertificatePath) != pem)
        {
            File.WriteAllText(PublicCaCertificatePath, pem);
        }
    }

    private static void TryRestrictToOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
