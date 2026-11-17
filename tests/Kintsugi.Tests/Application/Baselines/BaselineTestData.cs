using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Kintsugi.Application.Baselines;
using Kintsugi.Infrastructure.Persistence;
using Kintsugi.Infrastructure.Security;

namespace Kintsugi.Tests.Application.Baselines;

internal static class BaselineTestData
{
    public static BaselineItem Setting(string key, string data = "1", string valueName = "Value") =>
        new(key, "setting", "machine", "HKLM", @"SOFTWARE\Policies\Test", valueName, "dword", data, Mode: "audit", Reason: "test");

    public static BaselineItem Fact(string key) =>
        new(key, "fact", "machine", "HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID");

    public static ApplicationDbContext Context() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>The real signing service over a throwaway key directory, so a test signs exactly as the
    /// server does.</summary>
    public static ArtifactSigningService SigningService()
    {
        var root = Path.Combine(Path.GetTempPath(), "kintsugi-baseline-signing-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CaService:PrivateDirectory"] = Path.Combine(root, "private"),
                ["CaService:PublicDirectory"] = Path.Combine(root, "public"),
            })
            .Build();
        return new ArtifactSigningService(configuration, NullLogger<ArtifactSigningService>.Instance);
    }

    /// <summary>What the Windows agent's <c>identity::verify_artifact_signature</c> does: ECDSA-P256-SHA256,
    /// DER signature, over the document's UTF-8 bytes, against the pinned public key.</summary>
    public static bool AgentWouldAccept(string publicKeyPem, string document, string signatureBase64)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        return key.VerifyData(
            Encoding.UTF8.GetBytes(document), Convert.FromBase64String(signatureBase64),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }
}
