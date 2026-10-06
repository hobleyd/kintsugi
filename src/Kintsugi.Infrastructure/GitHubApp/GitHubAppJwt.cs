using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kintsugi.Infrastructure.GitHubApp;

/// <summary>
/// The short-lived JWT a GitHub App authenticates <em>as itself</em> with — the credential for
/// minting installation tokens and asking about its own installations, and nothing else.
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulled from a JWT library: it is one fixed header, three claims and an
/// RS256 signature, and doing it here keeps the App's private key inside one
/// <see cref="RSA"/> instance that is disposed as soon as the signature exists.
/// </remarks>
public static class GitHubAppJwt
{
    /// <summary>GitHub refuses a JWT whose lifetime exceeds ten minutes. Nine leaves room for the
    /// clock on this server running slightly fast of GitHub's.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(9);

    /// <summary>Backdated, as GitHub's own guidance recommends, so a clock running slightly slow
    /// does not produce a token that is "issued in the future" and rejected.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    public static string Create(long appId, string privateKeyPem, DateTimeOffset now)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = (now - ClockSkew).ToUnixTimeSeconds(),
            exp = (now + Lifetime).ToUnixTimeSeconds(),
            iss = appId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));

        var signingInput = $"{header}.{payload}";

        using var rsa = RSA.Create();
        // Accepts both PKCS#1 ("BEGIN RSA PRIVATE KEY", what GitHub issues) and PKCS#8.
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
