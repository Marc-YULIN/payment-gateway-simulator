using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaymentSim.Signing;

public static class CanonicalString
{
    /// <summary>Sorts ordinal keys, omits empty values and the signature field, and joins raw values without URL encoding.</summary>
    public static string Build(IReadOnlyDictionary<string, string?> fields, string signatureField = "sign") =>
        string.Join('&', fields.Where(x => x.Key != signatureField && !string.IsNullOrEmpty(x.Value))
            .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}"));
}

public sealed record SigningCredentials(string? SharedSecret = null, string? PrivateKeyPem = null, string? PublicKeyPem = null);

public interface ISignatureScheme
{
    string Name { get; }
    string Sign(IReadOnlyDictionary<string, string?> fields, SigningCredentials credentials);
    bool Verify(IReadOnlyDictionary<string, string?> fields, string signature, SigningCredentials credentials);
}

/// <summary>Legacy gateway compatibility only; MD5 is unsuitable for new integrations.</summary>
public sealed class Md5SortedKeyScheme : ISignatureScheme
{
    public string Name => "MD5";
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "Required solely for the documented legacy gateway compatibility scheme.")]
    public string Sign(IReadOnlyDictionary<string, string?> fields, SigningCredentials credentials) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(CanonicalString.Build(fields) + "&key=" +
            (credentials.SharedSecret ?? throw new ArgumentException("Shared secret is required.")))));
    public bool Verify(IReadOnlyDictionary<string, string?> fields, string signature, SigningCredentials credentials) =>
        SignatureComparison.Hex(Sign(fields, credentials), signature);
}

public sealed class HmacSha256Scheme : ISignatureScheme
{
    public string Name => "HMAC-SHA256";
    public string Sign(IReadOnlyDictionary<string, string?> fields, SigningCredentials credentials) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(credentials.SharedSecret ??
            throw new ArgumentException("Shared secret is required.")), Encoding.UTF8.GetBytes(CanonicalString.Build(fields))));
    public bool Verify(IReadOnlyDictionary<string, string?> fields, string signature, SigningCredentials credentials) =>
        SignatureComparison.Hex(Sign(fields, credentials), signature);
}

public sealed class RsaSha256Scheme : ISignatureScheme
{
    public string Name => "RSA-SHA256";
    public string Sign(IReadOnlyDictionary<string, string?> fields, SigningCredentials credentials)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(credentials.PrivateKeyPem ?? throw new ArgumentException("Private key is required."));
        return Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(CanonicalString.Build(fields)),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    public bool Verify(IReadOnlyDictionary<string, string?> fields, string signature, SigningCredentials credentials)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(credentials.PublicKeyPem ?? throw new ArgumentException("Public key is required."));
            var received = Convert.FromBase64String(signature);
            var parameters = rsa.ExportParameters(false);
            var modulus = new BigInteger(parameters.Modulus!, isUnsigned: true, isBigEndian: true);
            var representative = new BigInteger(received, isUnsigned: true, isBigEndian: true);
            if (received.Length != parameters.Modulus!.Length || representative >= modulus) return false;
            var decoded = BigInteger.ModPow(representative,
                new BigInteger(parameters.Exponent!, isUnsigned: true, isBigEndian: true), modulus)
                .ToByteArray(isUnsigned: true, isBigEndian: true);
            var actual = new byte[received.Length];
            decoded.CopyTo(actual.AsSpan(actual.Length - decoded.Length));
            var expected = new byte[received.Length];
            expected[1] = 1;
            var digestInfo = Convert.FromHexString("3031300D060960864801650304020105000420");
            var separator = expected.Length - digestInfo.Length - 32 - 1;
            if (separator < 10) return false;
            expected.AsSpan(2, separator - 2).Fill(0xff);
            digestInfo.CopyTo(expected.AsSpan(separator + 1));
            SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalString.Build(fields))).CopyTo(expected.AsSpan(expected.Length - 32));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }
}

internal static class SignatureComparison
{
    public static bool Hex(string expected, string actual)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual)); }
        catch (FormatException) { return false; }
    }
}

public static class SignatureSchemes
{
    public static ISignatureScheme Get(string name) => name.ToUpperInvariant() switch
    {
        "MD5" => new Md5SortedKeyScheme(),
        "HMAC-SHA256" => new HmacSha256Scheme(),
        "RSA-SHA256" => new RsaSha256Scheme(),
        _ => throw new NotSupportedException($"Unsupported signature scheme: {name}")
    };
}

public static class SignedPayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Produces flat camelCase fields. Decimal values use two invariant fractional digits; nested values are rejected.</summary>
    public static IDictionary<string, string?> FromObject<T>(T obj)
    {
        var element = JsonSerializer.SerializeToElement(obj, Options);
        if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("Payload must be an object.");
        return element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => p.Value.GetString(),
            JsonValueKind.Number => IsDecimal(obj, p.Name)
                ? p.Value.GetDecimal().ToString("F2", CultureInfo.InvariantCulture) : p.Value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new ArgumentException("Payload fields must be scalar values.")
        }, StringComparer.Ordinal);
    }

    private static bool IsDecimal<T>(T obj, string name)
    {
        var property = obj?.GetType().GetProperties().FirstOrDefault(p =>
            JsonNamingPolicy.CamelCase.ConvertName(p.Name) == name);
        return property is not null && (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(decimal);
    }
}
