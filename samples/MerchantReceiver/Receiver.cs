using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PaymentSim.Signing;

namespace MerchantReceiver;

public sealed record CheckoutRequest(decimal Amount, string Currency, string? Scenario = null);
public sealed record PayoutRequest(decimal Amount, string Currency, string BankCode, string AccountNo, string AccountName, string? Scenario = null);
public sealed record Notification(string OrderNo, string Status, string Outcome, DateTimeOffset ReceivedAt);

public sealed class LocalOrder(string merchantOrderNo, decimal amount, string currency, string type)
{
    public string MerchantOrderNo { get; } = merchantOrderNo;
    public decimal Amount { get; } = amount;
    public string Currency { get; } = currency;
    public string Type { get; } = type;
    public string? OrderNo { get; set; }
    public string Status { get; set; } = "Pending";
    public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
    public List<Notification> Notifications { get; } = [];
}

public sealed class Receiver(IHttpClientFactory clients, IOptions<GatewayOptions> gateway,
    IOptions<ReceiverOptions> receiver, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, LocalOrder> orders = new(StringComparer.Ordinal);

    public async Task<IResult> CreateAsync(CheckoutRequest request, PayoutRequest? payout, CancellationToken cancellationToken)
    {
        if (request.Amount <= 0 || request.Amount > 1_000_000_000m || decimal.Round(request.Amount, 2) != request.Amount ||
            request.Currency is not ("KRW" or "USD"))
            return Results.Problem(statusCode: 400, detail: "Invalid amount or currency.", extensions: new Dictionary<string, object?> { ["code"] = "invalid_order" });
        var merchantOrderNo = "DEMO-" + Guid.NewGuid().ToString("N");
        var local = new LocalOrder(merchantOrderNo, request.Amount, request.Currency, payout is null ? "PayIn" : "Payout");
        // Register before sending: a fast callback may arrive before the create response.
        orders[merchantOrderNo] = local;
        var fields = (Dictionary<string, string?>)SignedPayload.FromObject(new
        {
            gateway.Value.MerchantId, merchantOrderNo, request.Amount, request.Currency, request.Scenario,
            notifyUrl = receiver.Value.PublicBaseUrl.TrimEnd('/') + "/payments/notify",
            timestamp = clock.GetUtcNow().ToUnixTimeSeconds(), nonce = Guid.NewGuid().ToString("N")
        });
        if (payout is not null)
        {
            fields["bankCode"] = payout.BankCode;
            fields["accountNo"] = payout.AccountNo;
            fields["accountName"] = payout.AccountName;
        }
        var credentials = await CredentialsAsync(cancellationToken);
        var scheme = SignatureSchemes.Get(gateway.Value.Scheme);
        fields["sign"] = scheme.Sign(fields, credentials);
        using var response = await clients.CreateClient("gateway").PostAsJsonAsync(
            gateway.Value.BaseUrl.TrimEnd('/') + (payout is null ? "/gateway/payin" : "/gateway/payout"), fields, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var returned = ToFields(document.RootElement);
        // Verify the gateway even when the HTTP request succeeded; a status code is not authenticity proof.
        if (!returned.TryGetValue("sign", out var sign) || sign is null || !scheme.Verify(returned, sign, credentials))
            return Results.Problem(statusCode: 502, detail: "Gateway response signature verification failed.", extensions: new Dictionary<string, object?> { ["code"] = "untrusted_gateway" });
        if (!response.IsSuccessStatusCode) return Results.Json(returned, statusCode: (int)response.StatusCode);
        lock (local)
        {
            if (local.OrderNo is not null && local.OrderNo != returned["orderNo"])
                return Results.Problem(statusCode: 502, detail: "Gateway order identity changed.");
            local.OrderNo = returned["orderNo"];
        }
        return Results.Ok(returned);
    }

    public async Task<IResult> NotifyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        Dictionary<string, string?> fields;
        try
        {
            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync(cancellationToken);
                if (form.Any(x => x.Value.Count != 1)) return Fail();
                fields = form.ToDictionary(x => x.Key, x => (string?)x.Value.ToString(), StringComparer.Ordinal);
            }
            else
            {
                using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
                fields = ToFields(document.RootElement);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { return Fail(); }

        var credentials = await CredentialsAsync(cancellationToken);
        // Reject unauthenticated data before touching business state or the idempotency set.
        if (!fields.TryGetValue("sign", out var sign) || sign is null ||
            !SignatureSchemes.Get(gateway.Value.Scheme).Verify(fields, sign, credentials) ||
            fields.GetValueOrDefault("merchantId") != gateway.Value.MerchantId) return Fail();
        var merchantOrderNo = fields.GetValueOrDefault("merchantOrderNo");
        var orderNo = fields.GetValueOrDefault("orderNo");
        var status = fields.GetValueOrDefault("status");
        if (merchantOrderNo is null || orderNo is null || status is null || !orders.TryGetValue(merchantOrderNo, out var local)) return Fail();
        if (!decimal.TryParse(fields.GetValueOrDefault("amount"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ||
            amount != local.Amount || fields.GetValueOrDefault("currency") != local.Currency || fields.GetValueOrDefault("type") != local.Type ||
            !IsKnownStatus(local.Type, status)) return Fail();
        lock (local)
        {
            if (local.OrderNo is not null && local.OrderNo != orderNo) return Fail();
            local.OrderNo = orderNo;
            // A lock makes state update and orderNo+status deduplication one atomic operation in this in-memory sample.
            var outcome = !local.Seen.Add(orderNo + ":" + status) ? "duplicate" :
                IsTerminal(local.Status) || (local.Status == "Processing" && status == "Pending") ? "ignored" : "applied";
            if (outcome == "applied") local.Status = status;
            local.Notifications.Add(new Notification(orderNo, status, outcome, clock.GetUtcNow()));
            // Acknowledge only after the protected state change; production code must commit its database transaction first.
            return Results.Text("success");
        }
    }

    public object[] Snapshot() => orders.Values.Select(local =>
    {
        lock (local) return (object)new { local.MerchantOrderNo, local.OrderNo, local.Amount, local.Currency, local.Type,
            local.Status, notifications = local.Notifications.ToArray() };
    }).ToArray();

    private async Task<SigningCredentials> CredentialsAsync(CancellationToken cancellationToken) => new(gateway.Value.SharedSecret,
        gateway.Value.PrivateKeyPemPath is null ? null : await File.ReadAllTextAsync(gateway.Value.PrivateKeyPemPath, cancellationToken),
        gateway.Value.GatewayPublicKeyPemPath is null ? null : await File.ReadAllTextAsync(gateway.Value.GatewayPublicKeyPemPath, cancellationToken));

    private static bool IsTerminal(string status) => status is "Paid" or "Failed" or "Expired" or "Succeeded" or "Rejected";
    private static bool IsKnownStatus(string type, string status) => type == "PayIn"
        ? status is "Pending" or "Paid" or "Failed" or "Expired"
        : status is "Pending" or "Processing" or "Succeeded" or "Failed" or "Rejected";
    private static IResult Fail() => Results.Text("fail", statusCode: 400);

    private static Dictionary<string, string?> ToFields(JsonElement element) => element.EnumerateObject().ToDictionary(
        x => x.Name, x => x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() :
            x.Value.ValueKind == JsonValueKind.Null ? null : x.Value.GetRawText(), StringComparer.Ordinal);
}

public static class ReceiverEndpoints
{
    public static RouteGroupBuilder MapReceiver(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").WithTags("Merchant sample");
        group.MapPost("/checkout", (CheckoutRequest request, Receiver receiver, CancellationToken ct) =>
            receiver.CreateAsync(request, null, ct)).WithSummary("Create a signed pay-in and verify the response");
        group.MapPost("/payout", (PayoutRequest request, Receiver receiver, CancellationToken ct) =>
            receiver.CreateAsync(new CheckoutRequest(request.Amount, request.Currency, request.Scenario), request, ct))
            .WithSummary("Create a signed payout and verify the response");
        group.MapPost("/payments/notify", (HttpRequest request, Receiver receiver, CancellationToken ct) => receiver.NotifyAsync(request, ct))
            .WithSummary("Verify and atomically apply a callback before acknowledging it");
        group.MapGet("/orders", (Receiver receiver) => receiver.Snapshot()).WithSummary("Inspect local states and callback handling outcomes");
        return group;
    }
}
