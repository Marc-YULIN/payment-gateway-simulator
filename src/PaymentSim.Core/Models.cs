namespace PaymentSim.Core;

/// <summary>Wire encoding for callback fields.</summary>
public enum CallbackFormat { Form, Json }
/// <summary>PayIn collects funds; Payout sends reserved merchant funds.</summary>
public enum OrderType { PayIn, Payout }
/// <summary>Pending awaits action; Processing is payout-only; the other values are terminal.</summary>
public enum OrderStatus { Pending, Processing, Paid, Failed, Expired, Succeeded, Rejected }
/// <summary>Pending is available, InFlight is leased, Delivered is acknowledged, DeadLetter exhausted retries.</summary>
public enum OutboxState { Pending, InFlight, Delivered, DeadLetter }
/// <summary>Acknowledged requires the exact protocol acknowledgement; Rejected is an HTTP response; Error is transport failure.</summary>
public enum AttemptOutcome { Acknowledged, Rejected, Error }

public sealed class Merchant
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SignScheme { get; set; } = "HMAC-SHA256";
    public string? SharedSecret { get; set; }
    public string? MerchantPublicKeyPem { get; set; }
    public string? GatewayPrivateKeyPem { get; set; }
    public string? GatewayPublicKeyPem { get; set; }
    public CallbackFormat CallbackFormat { get; set; }
    public string DefaultScenario { get; set; } = "normal";
    public bool IsActive { get; set; } = true;
}

public sealed class MerchantBalance
{
    public string MerchantId { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal Available { get; set; }
    public long Version { get; set; }
}

public sealed record Payee(string BankCode, string AccountNo, string AccountName);

public sealed class PaymentOrder
{
    public string OrderNo { get; set; } = "";
    public string MerchantId { get; set; } = "";
    public string MerchantOrderNo { get; set; } = "";
    public OrderType Type { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public OrderStatus Status { get; set; }
    public string NotifyUrl { get; set; } = "";
    public string? ReturnUrl { get; set; }
    public string Scenario { get; set; } = "normal";
    public Payee? Payee { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset? NextActionAt { get; set; }
    public long Version { get; set; }
}

public sealed class CallbackOutbox
{
    public long Id { get; set; }
    public string OrderNo { get; set; } = "";
    public OrderStatus StatusSnapshot { get; set; }
    public string Payload { get; set; } = "";
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public OutboxState State { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public bool CorruptSignature { get; set; }
    public string? LeaseToken { get; set; }
}

public sealed class CallbackAttempt
{
    public long Id { get; set; }
    public long OutboxId { get; set; }
    public string OrderNo { get; set; } = "";
    public int AttemptNo { get; set; }
    public string RequestBody { get; set; } = "";
    public int? HttpStatus { get; set; }
    public string? ResponseBody { get; set; }
    public string? Error { get; set; }
    public long DurationMs { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public AttemptOutcome Outcome { get; set; }
}

public sealed record CreateOrderRequest(string MerchantId, string MerchantOrderNo, decimal Amount, string Currency,
    string NotifyUrl, string? ReturnUrl = null, string? Scenario = null, Payee? Payee = null);

public sealed class PaymentException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
