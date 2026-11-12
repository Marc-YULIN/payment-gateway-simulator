using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentSim.Core;
using PaymentSim.Infrastructure;

namespace PaymentSim.Api.Endpoints;

public sealed record TopUpRequest(string Currency, decimal Amount);

public static class AdminEndpoints
{
    public static bool IsValidKey(string? actual, string expected) => actual is not null &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(actual)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    public static RouteGroupBuilder MapAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/api").WithTags("Administration");
        group.AddEndpointFilter(async (context, next) =>
        {
            var key = context.HttpContext.Request.Headers["X-Admin-Key"].ToString();
            var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<SimulatorOptions>>();
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!IsValidKey(key, options.Value.AdminKey))
                throw new PaymentException("9999", "Invalid admin key.", 401);
            return await next(context);
        });
        group.MapGet("/orders", async (PaymentSimDbContext db, CancellationToken ct) =>
            (await db.Orders.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(ct)).Select(OrderView))
            .WithSummary("List the latest 200 orders with masked payees");
        group.MapGet("/orders/{orderNo}", async (string orderNo, PaymentSimDbContext db, CancellationToken ct) =>
        {
            var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.OrderNo == orderNo, ct)
                ?? throw new PaymentException("9999", "Order was not found.", 404);
            var attempts = await db.Attempts.AsNoTracking().Where(x => x.OrderNo == orderNo).OrderBy(x => x.Id).ToListAsync(ct);
            var outbox = await db.Outbox.AsNoTracking().Where(x => x.OrderNo == orderNo)
                .Select(x => new { x.Id, x.StatusSnapshot, x.State, x.AttemptCount, x.NextAttemptAt }).ToListAsync(ct);
            return Results.Ok(new { order = OrderView(order), callbacks = attempts, outbox });
        }).WithSummary("Inspect one order and its delivery history");
        group.MapPost("/orders/{orderNo}/resend-callback", async (string orderNo, OrderService orders, CancellationToken ct) =>
        {
            await orders.ResendAsync(orderNo, ct);
            return Results.Accepted();
        }).WithSummary("Enqueue a correctly signed callback of the current state");
        group.MapGet("/callbacks", async (string? outcome, PaymentSimDbContext db, CancellationToken ct) =>
        {
            var query = db.Attempts.AsNoTracking();
            if (!string.IsNullOrEmpty(outcome))
            {
                if (!Enum.TryParse<AttemptOutcome>(outcome, true, out var parsed) || !Enum.IsDefined(parsed))
                    throw new PaymentException("9999", "Unknown callback outcome.", 400);
                query = query.Where(x => x.Outcome == parsed);
            }
            return Results.Ok(await query.OrderByDescending(x => x.Id).Take(200).ToListAsync(ct));
        }).WithSummary("List the latest 200 callback attempts by outcome");
        group.MapGet("/merchants", async (PaymentSimDbContext db, CancellationToken ct) =>
            Results.Ok(new
            {
                merchants = await db.Merchants.AsNoTracking().Select(x => new { x.Id, x.Name, x.SignScheme, x.CallbackFormat,
                    x.DefaultScenario, x.IsActive, x.GatewayPublicKeyPem }).ToListAsync(ct),
                balances = await db.Balances.AsNoTracking().ToListAsync(ct)
            })).WithSummary("List merchants and balances without private credentials");
        group.MapPost("/merchants/{id}/balance", async (string id, TopUpRequest request, PaymentSimDbContext db, CancellationToken ct) =>
        {
            if (request.Amount <= 0 || request.Amount > 1_000_000_000m || decimal.Round(request.Amount, 2) != request.Amount)
                throw new PaymentException("9999", "Invalid top-up amount.", 400);
            var balance = await db.Balances.SingleOrDefaultAsync(x => x.MerchantId == id && x.Currency == request.Currency, ct)
                ?? throw new PaymentException("9999", "Balance was not found.", 404);
            if (balance.Available > 1_000_000_000m - request.Amount) throw new PaymentException("9999", "Balance limit exceeded.", 422);
            balance.Available += request.Amount;
            balance.Version++;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { throw new PaymentException("9999", "Balance changed; retry the top-up.", 409); }
            return Results.Ok(balance);
        }).WithSummary("Add synthetic funds to a merchant balance");
        return group;
    }

    private static object OrderView(PaymentOrder order) => new
    {
        order.OrderNo, order.MerchantId, order.MerchantOrderNo, order.Type, order.Amount, order.Currency,
        order.Status, order.Scenario, order.CreatedAt, order.ExpiresAt, order.CompletedAt, order.FailureReason,
        payee = order.Payee is null ? null : new { order.Payee.BankCode, accountNo = "****" + order.Payee.AccountNo[^Math.Min(4, order.Payee.AccountNo.Length)..] }
    };
}
