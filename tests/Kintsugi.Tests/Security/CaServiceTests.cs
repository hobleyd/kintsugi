using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Kintsugi.Infrastructure.Security;

namespace Kintsugi.Tests.Security;

public class CaServiceTests : IDisposable
{
    private readonly string _privateDir;
    private readonly string _publicDir;

    public CaServiceTests()
    {
        _privateDir = Directory.CreateTempSubdirectory("kintsugi-ca-priv-").FullName;
        _publicDir = Directory.CreateTempSubdirectory("kintsugi-ca-pub-").FullName;
    }

    public void Dispose()
    {
        Directory.Delete(_privateDir, recursive: true);
        Directory.Delete(_publicDir, recursive: true);
    }

    private CaService CreateService() =>
        new(BuildConfiguration(_privateDir, _publicDir), NullLogger<CaService>.Instance);

    private static IConfiguration BuildConfiguration(string privateDir, string publicDir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CaService:PrivateDirectory"] = privateDir,
                ["CaService:PublicDirectory"] = publicDir,
            })
            .Build();

    /// <summary>Builds a syntactically valid, self-signed-style PKCS#10 CSR PEM, as an agent would
    /// generate locally — deliberately requesting a bogus subject, to prove CaService ignores it.</summary>
    private static string GenerateCsrPem(string requestedCommonName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={requestedCommonName}", key, HashAlgorithmName.SHA256);
        var der = request.CreateSigningRequest();
        return new string(PemEncoding.Write("CERTIFICATE REQUEST", der));
    }

    [Fact]
    public void IssueClientCertificatePem_BindsToTheRequestedCommonName_NotWhateverTheCsrItselfAsked()
    {
        var service = CreateService();
        var csr = GenerateCsrPem(requestedCommonName: "totally-different-identity");

        var issuedPem = service.IssueClientCertificatePem(csr, "REAL-SERIAL-123", TimeSpan.FromDays(1));

        using var issued = X509Certificate2.CreateFromPem(issuedPem);
        Assert.Equal("CN=REAL-SERIAL-123", issued.Subject);
    }

    [Fact]
    public void IssueClientCertificatePem_ProducesACertificateThatChainsToTheReturnedCa()
    {
        var service = CreateService();
        var csr = GenerateCsrPem("irrelevant");

        var issuedPem = service.IssueClientCertificatePem(csr, "HOST-1", TimeSpan.FromDays(1));
        var caPem = service.GetCaCertificatePem();

        using var issued = X509Certificate2.CreateFromPem(issuedPem);
        using var ca = X509Certificate2.CreateFromPem(caPem);

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        Assert.True(chain.Build(issued), "issued certificate should chain to the CA CaService itself returned");
    }

    [Fact]
    public void IssueClientCertificatePem_SetsClientAuthenticationExtendedKeyUsage()
    {
        var service = CreateService();
        var issuedPem = service.IssueClientCertificatePem(GenerateCsrPem("x"), "HOST-2", TimeSpan.FromDays(1));

        using var issued = X509Certificate2.CreateFromPem(issuedPem);
        var eku = issued.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();

        Assert.Contains(eku.EnhancedKeyUsages.Cast<Oid>(), oid => oid.Value == "1.3.6.1.5.5.7.3.2");
    }

    [Fact]
    public void IssueClientCertificatePem_ThrowsForAMalformedCsr()
    {
        var service = CreateService();

        Assert.Throws<CryptographicException>(() => service.IssueClientCertificatePem("not a real CSR", "HOST-3", TimeSpan.FromDays(1)));
    }

    [Fact]
    public void GetCaCertificatePem_ReusesThePersistedCaAcrossInstances_RatherThanRegeneratingIt()
    {
        var first = CreateService();
        var firstCa = first.GetCaCertificatePem();

        // A fresh instance pointed at the same directories — simulating the api container
        // restarting — must load the same CA back rather than minting a new one, or every
        // already-enrolled agent's certificate would stop chaining to it.
        var second = CreateService();
        var secondCa = second.GetCaCertificatePem();

        Assert.Equal(firstCa, secondCa);
    }

    [Fact]
    public void GetCaCertificatePem_IsWrittenToThePublicDirectory_ForNginxToReadWithoutThePrivateKey()
    {
        var service = CreateService();
        var caPem = service.GetCaCertificatePem();

        var publicCopyPath = Path.Combine(_publicDir, "ca.crt");
        Assert.True(File.Exists(publicCopyPath));
        Assert.Equal(caPem, File.ReadAllText(publicCopyPath));
        Assert.False(File.Exists(Path.Combine(_publicDir, "ca.key")), "the CA's private key must never be written to the public directory");
    }
}
