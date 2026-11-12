using System.ComponentModel.DataAnnotations;

namespace MerchantReceiver;

public sealed class GatewayOptions
{
    [Required, Url] public string BaseUrl { get; set; } = "http://localhost:5102";
    [Required] public string MerchantId { get; set; } = "M1001";
    [Required] public string Scheme { get; set; } = "HMAC-SHA256";
    public string? SharedSecret { get; set; } = "demo-hmac-secret";
    public string? PrivateKeyPemPath { get; set; }
    public string? GatewayPublicKeyPemPath { get; set; }
}

public sealed class ReceiverOptions
{
    [Required, Url] public string PublicBaseUrl { get; set; } = "http://localhost:5112";
}
