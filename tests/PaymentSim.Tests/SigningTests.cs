using System.Globalization;
using System.Security.Cryptography;
using PaymentSim.Signing;

namespace PaymentSim.Tests;

public sealed class SigningTests
{
    [Fact]
    public void Build_UnsortedFields_ProducesKnownCanonicalString()
    {
        var fields = new Dictionary<string, string?> { ["z"] = "hello & world", ["A"] = "1", ["a"] = "2", ["empty"] = "", ["null"] = null, ["sign"] = "ignored" };
        Assert.Equal("A=1&a=2&z=hello & world", CanonicalString.Build(fields));
        Assert.Equal("A=1&a=2&sign=ignored", CanonicalString.Build(fields, "z"));
    }

    [Theory]
    [InlineData("MD5")]
    [InlineData("HMAC-SHA256")]
    [InlineData("RSA-SHA256")]
    public void Sign_ValidTamperedReorderedAndWrongKey_VerifiesOnlyAuthenticFields(string name)
    {
        using var rsa = RSA.Create(2048);
        using var wrongRsa = RSA.Create(2048);
        var credentials = new SigningCredentials("secret", rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
        var wrong = new SigningCredentials("wrong", wrongRsa.ExportPkcs8PrivateKeyPem(), wrongRsa.ExportSubjectPublicKeyInfoPem());
        var scheme = SignatureSchemes.Get(name.ToLowerInvariant());
        Assert.Equal(name, scheme.Name);
        var fields = new Dictionary<string, string?> { ["amount"] = "10.00", ["merchantId"] = "M1001" };
        var signature = scheme.Sign(fields, credentials);
        Assert.True(scheme.Verify(fields, signature, credentials));
        var reordered = new Dictionary<string, string?> { ["merchantId"] = "M1001", ["amount"] = "10.00", ["empty"] = "", ["null"] = null, ["sign"] = "ignored" };
        Assert.Equal(signature, scheme.Sign(reordered, credentials));
        Assert.False(scheme.Verify(fields, signature, wrong));
        foreach (var key in fields.Keys)
        {
            var changed = new Dictionary<string, string?>(fields) { [key] = "tampered" };
            Assert.False(scheme.Verify(changed, signature, credentials));
        }
        Assert.False(scheme.Verify(fields, "invalid!", credentials));
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "Verifies the required legacy compatibility digest.")]
    public void Sign_KnownInput_MatchesIndependentDigestVectors()
    {
        var fields = new Dictionary<string, string?> { ["a"] = "1" };
        Assert.Equal(Convert.ToHexString(MD5.HashData("a=1&key=secret"u8)), new Md5SortedKeyScheme().Sign(fields, new("secret")));
        Assert.Equal(Convert.ToHexStringLower(HMACSHA256.HashData("secret"u8, "a=1"u8)), new HmacSha256Scheme().Sign(fields, new("secret")));
        using var rsa = RSA.Create(2048);
        var credentials = new SigningCredentials(PrivateKeyPem: rsa.ExportPkcs8PrivateKeyPem(), PublicKeyPem: rsa.ExportSubjectPublicKeyInfoPem());
        var signature = new RsaSha256Scheme().Sign(fields, credentials);
        Assert.True(rsa.VerifyData("a=1"u8, Convert.FromBase64String(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.True(new RsaSha256Scheme().Verify(fields, Convert.ToBase64String(rsa.SignData("a=1"u8, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)), credentials));
    }

    [Fact]
    public void FromObject_DifferentCulture_PreservesStableScalarFields()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var fields = SignedPayload.FromObject(new { MerchantId = "M1001", Amount = 12.5m, Timestamp = 123L, Missing = (string?)null });
            Assert.Equal("12.50", fields["amount"]);
            Assert.Equal("123", fields["timestamp"]);
            Assert.Null(fields["missing"]);
            Assert.Equal("M1001", fields["merchantId"]);
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Throws<NotSupportedException>(() => SignatureSchemes.Get("unknown"));
    }
}
