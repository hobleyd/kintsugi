using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Kintsugi.Infrastructure.Security;

namespace Kintsugi.Tests.Security;

public class ArtifactSigningServiceTests : IDisposable
{
    private readonly string _privateDir;
    private readonly string _publicDir;

    public ArtifactSigningServiceTests()
    {
        _privateDir = Directory.CreateTempSubdirectory("kintsugi-sign-priv-").FullName;
        _publicDir = Directory.CreateTempSubdirectory("kintsugi-sign-pub-").FullName;
    }

    public void Dispose()
    {
        Directory.Delete(_privateDir, recursive: true);
        Directory.Delete(_publicDir, recursive: true);
    }

    private ArtifactSigningService CreateService() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CaService:PrivateDirectory"] = _privateDir,
                ["CaService:PublicDirectory"] = _publicDir,
            })
            .Build(),
        NullLogger<ArtifactSigningService>.Instance);

    private static bool VerifyWithRawEcdsa(string publicKeyPem, string content, string signatureBase64)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        var signature = Convert.FromBase64String(signatureBase64);
        return key.VerifyData(Encoding.UTF8.GetBytes(content), signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sign_ReturnsNull_ForNullOrEmptyContent(string? content)
    {
        var service = CreateService();

        Assert.Null(service.Sign(content));
    }

    [Fact]
    public void Sign_ProducesASignatureThatVerifiesAgainstThePublicKey()
    {
        var service = CreateService();
        const string script = "#!/bin/sh\necho hello\n";

        var signature = service.Sign(script);

        Assert.NotNull(signature);
        Assert.True(VerifyWithRawEcdsa(service.GetPublicKeyPem(), script, signature!), "a genuine signature over the exact content must verify");
    }

    [Fact]
    public void Sign_TamperedContentFailsVerification()
    {
        var service = CreateService();
        var signature = service.Sign("#!/bin/sh\necho hello\n")!;

        var verifies = VerifyWithRawEcdsa(service.GetPublicKeyPem(), "#!/bin/sh\necho PWNED\n", signature);

        Assert.False(verifies, "a signature must not verify against content it wasn't produced for");
    }

    [Fact]
    public void Sign_DifferentContentProducesDifferentSignatures()
    {
        var service = CreateService();

        var signatureA = service.Sign("content A");
        var signatureB = service.Sign("content B");

        Assert.NotEqual(signatureA, signatureB);
    }

    [Fact]
    public void GetPublicKeyPem_ReusesThePersistedKeyAcrossInstances_RatherThanRegeneratingIt()
    {
        var first = CreateService();
        var firstKey = first.GetPublicKeyPem();

        // A fresh instance pointed at the same directories — simulating the api container
        // restarting — must load the same key back, or every previously issued signature would
        // stop verifying.
        var second = CreateService();
        var secondKey = second.GetPublicKeyPem();

        Assert.Equal(firstKey, secondKey);
    }
}
