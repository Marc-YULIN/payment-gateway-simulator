using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using PaymentSim.Core;
using PaymentSim.Signing;

namespace PaymentSim.Infrastructure;

public sealed class RequestAuthenticator(IMemoryCache cache, TimeProvider timeProvider)
{
    private static readonly object NonceLock = new();

    public void Authenticate(Merchant merchant, IReadOnlyDictionary<string, string?> fields)
    {
        if (!merchant.IsActive || !fields.TryGetValue("sign", out var signature) || string.IsNullOrEmpty(signature) ||
            !SignatureSchemes.Get(merchant.SignScheme).Verify(fields, signature,
                new SigningCredentials(merchant.SharedSecret, PublicKeyPem: merchant.MerchantPublicKeyPem)))
            throw new PaymentException("1001", "Invalid signature or inactive merchant.", 401);
        if (!fields.TryGetValue("timestamp", out var timestamp) ||
            !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            Math.Abs((decimal)timeProvider.GetUtcNow().ToUnixTimeSeconds() - seconds) > 300)
            throw new PaymentException("1002", "Timestamp is outside the allowed window.", 401);
        if (!fields.TryGetValue("nonce", out var nonce) || string.IsNullOrWhiteSpace(nonce) || nonce.Length > 128)
            throw new PaymentException("9999", "A nonce of up to 128 characters is required.", 400);
        // The check and insert must be atomic within this single-process sandbox.
        lock (NonceLock)
        {
            var key = (merchant.Id, nonce);
            if (cache.TryGetValue(key, out _)) throw new PaymentException("1003", "Nonce has already been used.", 409);
            cache.Set(key, true, TimeSpan.FromMinutes(10));
        }
    }
}
