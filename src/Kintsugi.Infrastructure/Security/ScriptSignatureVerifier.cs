using System.Security.Cryptography;
using System.Text;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.Security;

/// <inheritdoc cref="IScriptSignatureVerifier" />
public class ScriptSignatureVerifier : IScriptSignatureVerifier
{
    public bool Verify(string script, string base64Signature, string publicKeyPem)
    {
        if (string.IsNullOrEmpty(script) || string.IsNullOrWhiteSpace(base64Signature) || string.IsNullOrWhiteSpace(publicKeyPem))
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);

            // Rfc3279DerSequence to match ArtifactSigningService.Sign's explicit DER output. Getting
            // this wrong wouldn't error — a raw P1363 r||s of the right length simply fails to
            // verify — so every signature would look forged and no entry would ever import.
            return key.VerifyData(
                Encoding.UTF8.GetBytes(script),
                Convert.FromBase64String(base64Signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            // A malformed key, a malformed signature and an honest mismatch all mean the same thing
            // to every caller — don't import this entry — so none of them is worth distinguishing
            // by throwing.
            return false;
        }
    }
}
