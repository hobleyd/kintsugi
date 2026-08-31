using System.Security.Cryptography;
using Kintsugi.Application.ScriptApproval;
using Kintsugi.Infrastructure.Security;

namespace Kintsugi.Tests.ScriptApproval;

public class ScriptSignerFingerprintTests
{
    [Fact]
    public void For_MatchesTheDigestOfTheKeysOwnSubjectPublicKeyInfoDer()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var expected = "sha256:" + Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        // Computed off the PEM text rather than by importing the key, so this asserts the pure-text
        // path agrees with what the crypto API would have produced.
        Assert.Equal(expected, ScriptSignerFingerprint.For(key.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void For_ToleratesCarriageReturnsAndSurroundingWhitespace()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();

        // This text has travelled through a JSON document and a git checkout to get here.
        Assert.Equal(ScriptSignerFingerprint.For(pem), ScriptSignerFingerprint.For("\n  " + pem.Replace("\n", "\r\n") + "  \n"));
    }

    [Fact]
    public void Bare_StripsThePrefixSoTheFingerprintCanBeAPathSegment()
    {
        // A colon is legal in a git path but awkward in a checkout on Windows, which is why the
        // filename drops the prefix while the canonical form keeps it.
        Assert.Equal("abcdef", ScriptSignerFingerprint.Bare("sha256:abcdef"));
        Assert.Equal("abcdef", ScriptSignerFingerprint.Bare("abcdef"));
    }
}

public class ScriptSignatureVerifierTests
{
    private readonly ScriptSignatureVerifier _verifier = new();

    [Fact]
    public void Verify_AcceptsASignatureInTheFormatArtifactSigningServiceProduces()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var script = "#!/bin/bash\nexit 0\n";
        // Rfc3279DerSequence, matching ArtifactSigningService.Sign. A raw P1363 r||s of the right
        // length simply fails to verify rather than erroring, so getting this wrong would make every
        // signature look forged and no entry would ever import.
        var signature = Convert.ToBase64String(key.SignData(
            System.Text.Encoding.UTF8.GetBytes(script), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        Assert.True(_verifier.Verify(script, signature, key.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void Verify_RejectsASignatureOverDifferentBytes()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = Convert.ToBase64String(key.SignData(
            System.Text.Encoding.UTF8.GetBytes("#!/bin/bash\nexit 0\n"), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        Assert.False(_verifier.Verify("#!/bin/bash\nrm -rf /\n", signature, key.ExportSubjectPublicKeyInfoPem()));
    }

    [Theory]
    [InlineData("not base64 at all")]
    [InlineData("")]
    public void Verify_ReturnsFalseRatherThanThrowingOnMalformedInput(string signature)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // A malformed key, a malformed signature and an honest mismatch all mean the same thing to
        // every caller — don't import this entry — so none of them is worth distinguishing.
        Assert.False(_verifier.Verify("#!/bin/bash\n", signature, key.ExportSubjectPublicKeyInfoPem()));
        Assert.False(_verifier.Verify("#!/bin/bash\n", "MEUCIQ==", "-----BEGIN PUBLIC KEY-----\nnope\n-----END PUBLIC KEY-----\n"));
    }
}
