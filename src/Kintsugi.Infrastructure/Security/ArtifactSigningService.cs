using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ScriptApproval;

namespace Kintsugi.Infrastructure.Security;

/// <summary>
/// Generates (once) and persists a dedicated ECDSA keypair used only to sign the content agents
/// execute unattended — kept separate from <see cref="CaService"/>'s CA key so that a key which
/// merely vouches for script/command content carries none of the blast radius of one that can mint
/// new trusted agent identities. Storage layout mirrors CaService: the private key lives only
/// under <c>CaService:PrivateDirectory</c> (the api-only agent-ca-private volume); the public key
/// is mirrored to <c>CaService:PublicDirectory</c>, though the primary way an agent actually gets
/// it is the enrollment response (see <c>EnrollAgentCommandHandler</c>), not that file directly.
/// </summary>
public class ArtifactSigningService : IArtifactSigningService
{
    private readonly string _privateDirectory;
    private readonly string _publicDirectory;
    private readonly ILogger<ArtifactSigningService> _logger;
    private readonly object _lock = new();
    private ECDsa? _key;

    public ArtifactSigningService(IConfiguration configuration, ILogger<ArtifactSigningService> logger)
    {
        _privateDirectory = configuration["CaService:PrivateDirectory"] ?? "/data/agent-ca-private";
        _publicDirectory = configuration["CaService:PublicDirectory"] ?? "/data/agent-ca-public";
        _logger = logger;
    }

    private string PrivateKeyPath => Path.Combine(_privateDirectory, "artifact-signing.key");
    private string PublicKeyPath => Path.Combine(_publicDirectory, "artifact-signing.pub");

    public string GetPublicKeyPem() => LoadOrCreateKey().ExportSubjectPublicKeyInfoPem();

    public string GetPublicKeyFingerprint() => ScriptSignerFingerprint.For(GetPublicKeyPem());

    public string? Sign(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        // Explicit DER (the standard ASN.1 ECDSA-Sig-Value SEQUENCE{r,s}) rather than this
        // overload's default IEEE P1363 raw r||s — DER is what every other ECDSA implementation
        // (OpenSSL, the kintsugi-agent's own verification) expects unless told otherwise, so this
        // avoids a signature format mismatch between the two ends of the same check.
        var signature = LoadOrCreateKey().SignData(Encoding.UTF8.GetBytes(content), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Convert.ToBase64String(signature);
    }

    private ECDsa LoadOrCreateKey()
    {
        if (_key is not null)
        {
            return _key;
        }

        lock (_lock)
        {
            if (_key is not null)
            {
                return _key;
            }

            Directory.CreateDirectory(_privateDirectory);
            Directory.CreateDirectory(_publicDirectory);

            if (File.Exists(PrivateKeyPath))
            {
                var key = ECDsa.Create();
                key.ImportFromPem(File.ReadAllText(PrivateKeyPath));
                _key = key;
                EnsurePublicCopy(_key);
                return _key;
            }

            _logger.LogInformation("No artifact-signing key found under {PrivateDirectory} — generating a new one.", _privateDirectory);

            var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(PrivateKeyPath, newKey.ExportPkcs8PrivateKeyPem());
            TryRestrictToOwnerOnly(PrivateKeyPath);

            _key = newKey;
            EnsurePublicCopy(_key);
            return _key;
        }
    }

    private void EnsurePublicCopy(ECDsa key)
    {
        var pem = key.ExportSubjectPublicKeyInfoPem();
        if (!File.Exists(PublicKeyPath) || File.ReadAllText(PublicKeyPath) != pem)
        {
            File.WriteAllText(PublicKeyPath, pem);
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
