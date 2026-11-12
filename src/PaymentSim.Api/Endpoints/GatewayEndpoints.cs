using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentSim.Core;
using PaymentSim.Infrastructure;
using PaymentSim.Signing;

namespace PaymentSim.Api.Endpoints;

public static class GatewayEndpoints
{
    public static RouteGroupBuilder MapGateway(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gateway").WithTags("Gateway");
        group.MapPost("/payin", (HttpContext context, PaymentSimDbContext db, RequestAuthenticator auth,
            OrderService orders, IOptions<SimulatorOptions> options, CancellationToken ct) =>
            CreateAsync(context, db, auth, orders, options.Value, OrderType.PayIn, ct)).WithSummary("Create an idempotent pay-in order");
        group.MapPost("/payout", (HttpContext context, PaymentSimDbContext db, RequestAuthenticator auth,
            OrderService orders, IOptions<SimulatorOptions> options, CancellationToken ct) =>
            CreateAsync(context, db, auth, orders, options.Value, OrderType.Payout, ct)).WithSummary("Reserve funds and create a payout");
        group.MapPost("/query", QueryAsync).WithSummary("Query an order using a signed request");
        return group;
    }

    private static async Task<IResult> CreateAsync(HttpContext context, PaymentSimDbContext db, RequestAuthenticator auth,
        OrderService orders, SimulatorOptions options, OrderType type, CancellationToken ct)
    {
        var (fields, merchant) = await AuthenticateAsync(context, db, auth, ct);
        if (!decimal.TryParse(Required(fields, "amount"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
            throw new PaymentException("9999", "Invalid amount.", 400);
        fields.TryGetValue("returnUrl", out var returnUrl);
        fields.TryGetValue("scenario", out var scenario);
        if (context.Request.Headers.TryGetValue("X-Sim-Scenario", out var header)) scenario = header.ToString();
        var request = new CreateOrderRequest(merchant.Id, Required(fields, "merchantOrderNo"), amount,
            Required(fields, "currency"), Required(fields, "notifyUrl"), returnUrl, scenario,
            type == OrderType.Payout ? new Payee(Required(fields, "bankCode"), Required(fields, "accountNo"), Required(fields, "accountName")) : null);
        var order = type == OrderType.PayIn ? await orders.CreatePayInAsync(request, ct) : await orders.CreatePayoutAsync(request, ct);
        var response = new Dictionary<string, string?> { ["code"] = "0000", ["orderNo"] = order.OrderNo, ["status"] = order.Status.ToString() };
        if (type == OrderType.PayIn) response["cashierUrl"] = $"{options.PublicBaseUrl.TrimEnd('/')}/cashier/{order.OrderNo}";
        return Results.Ok(Sign(response, merchant));
    }

    private static async Task<IResult> QueryAsync(HttpContext context, PaymentSimDbContext db, RequestAuthenticator auth, CancellationToken ct)
    {
        var (fields, merchant) = await AuthenticateAsync(context, db, auth, ct);
        fields.TryGetValue("merchantOrderNo", out var merchantOrderNo);
        fields.TryGetValue("orderNo", out var orderNo);
        if (string.IsNullOrWhiteSpace(merchantOrderNo) && string.IsNullOrWhiteSpace(orderNo))
            throw new PaymentException("9999", "An order identifier is required.", 400);
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.MerchantId == merchant.Id &&
            (orderNo == null || x.OrderNo == orderNo) && (merchantOrderNo == null || x.MerchantOrderNo == merchantOrderNo), ct)
            ?? throw new PaymentException("9999", "Order was not found.", 404);
        return Results.Ok(Sign((Dictionary<string, string?>)SignedPayload.FromObject(new
        {
            code = "0000", order.OrderNo, order.MerchantOrderNo, order.Amount, order.Currency,
            type = order.Type.ToString(), status = order.Status.ToString()
        }), merchant));
    }

    private static async Task<(Dictionary<string, string?> Fields, Merchant Merchant)> AuthenticateAsync(HttpContext context,
        PaymentSimDbContext db, RequestAuthenticator auth, CancellationToken ct)
    {
        var fields = await ReadFieldsAsync(context.Request, ct);
        var id = Required(fields, "merchantId");
        var merchant = await db.Merchants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new PaymentException("1001", "Unknown merchant.", 401);
        context.Items["merchant"] = merchant;
        auth.Authenticate(merchant, fields);
        return (fields, merchant);
    }

    public static Dictionary<string, string?> Sign(Dictionary<string, string?> fields, Merchant merchant)
    {
        fields["sign"] = SignatureSchemes.Get(merchant.SignScheme).Sign(fields,
            new SigningCredentials(merchant.SharedSecret, merchant.GatewayPrivateKeyPem));
        return fields;
    }

    public static async Task<Dictionary<string, string?>> ReadFieldsAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            if (form.Any(x => x.Value.Count != 1)) throw new PaymentException("9999", "Repeated fields are not supported.", 400);
            return form.ToDictionary(x => x.Key, x => (string?)x.Value.ToString(), StringComparer.Ordinal);
        }
        using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new PaymentException("9999", "Expected a flat object.", 400);
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null => null,
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => throw new PaymentException("9999", "Fields must be strings, numbers or null.", 400)
            };
            if (!fields.TryAdd(property.Name, value)) throw new PaymentException("9999", "Repeated fields are not supported.", 400);
        }
        return fields;
    }

    private static string Required(Dictionary<string, string?> fields, string name) =>
        fields.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value :
            throw new PaymentException("9999", $"Missing field: {name}.", 400);
}
