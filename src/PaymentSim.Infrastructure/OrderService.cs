using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PaymentSim.Core;

namespace PaymentSim.Infrastructure;

public sealed class OrderService(PaymentSimDbContext db, CallbackScheduler scheduler, TimeProvider clock)
{
    public Task<PaymentOrder> CreatePayInAsync(CreateOrderRequest request, CancellationToken cancellationToken) =>
        CreateAsync(request, OrderType.PayIn, cancellationToken);
    public Task<PaymentOrder> CreatePayoutAsync(CreateOrderRequest request, CancellationToken cancellationToken) =>
        CreateAsync(request, OrderType.Payout, cancellationToken);

    private async Task<PaymentOrder> CreateAsync(CreateOrderRequest request, OrderType type, CancellationToken cancellationToken)
    {
        Validate(request, type);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var existing = await db.Orders.SingleOrDefaultAsync(x => x.MerchantId == request.MerchantId &&
                    x.MerchantOrderNo == request.MerchantOrderNo, cancellationToken);
                if (existing is not null)
                {
                    if (!Matches(existing, request, type)) throw new PaymentException("2001", "duplicate_merchant_order", 409);
                    return existing;
                }
                var merchant = await db.Merchants.SingleAsync(x => x.Id == request.MerchantId, cancellationToken);
                var scenarioText = request.Scenario ?? merchant.DefaultScenario;
                _ = ScenarioParser.Parse(scenarioText);
                if (type == OrderType.Payout)
                {
                    var balance = await db.Balances.SingleOrDefaultAsync(x => x.MerchantId == request.MerchantId &&
                        x.Currency == request.Currency, cancellationToken);
                    if (balance is null || balance.Available < request.Amount)
                        throw new PaymentException("3001", "insufficient_balance", 422);
                    balance.Available -= request.Amount;
                    balance.Version++;
                }
                var now = clock.GetUtcNow();
                var order = new PaymentOrder
                {
                    OrderNo = $"{(type == OrderType.PayIn ? 'P' : 'W')}{now:yyyyMMdd}{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}",
                    MerchantId = request.MerchantId, MerchantOrderNo = request.MerchantOrderNo,
                    Type = type, Amount = request.Amount, Currency = request.Currency, NotifyUrl = request.NotifyUrl,
                    ReturnUrl = request.ReturnUrl, Scenario = scenarioText, Payee = request.Payee,
                    CreatedAt = now, ExpiresAt = now.AddMinutes(15), NextActionAt = type == OrderType.Payout ? now.AddSeconds(2) : null
                };
                db.Orders.Add(order);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return order;
            }
            catch (Exception ex) when (attempt < 3 && IsWriteConflict(ex))
            {
                db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), clock, cancellationToken);
            }
        }
    }

    public async Task<PaymentOrder> CompletePayInAsync(string orderNo, bool success, CancellationToken cancellationToken)
    {
        return await MutateAsync(orderNo, async order =>
        {
            if (order.Type != OrderType.PayIn) throw new PaymentException("9999", "Not a pay-in order.", 400);
            if (order.Status != OrderStatus.Pending) return;
            var status = clock.GetUtcNow() >= order.ExpiresAt ? OrderStatus.Expired :
                success && !ScenarioParser.Parse(order.Scenario).Decline ? OrderStatus.Paid : OrderStatus.Failed;
            await TransitionAsync(order, status, cancellationToken);
        }, cancellationToken);
    }

    public async Task<PaymentOrder> CompletePayoutAsync(string orderNo, bool success, CancellationToken cancellationToken) =>
        await MutateAsync(orderNo, async order =>
        {
            if (order.Type != OrderType.Payout || order.Status != OrderStatus.Processing)
                throw new PaymentException("9999", "Payout is not processing.", 409);
            await TransitionAsync(order, success ? OrderStatus.Succeeded : OrderStatus.Failed, cancellationToken);
        }, cancellationToken);

    public async Task ProcessDuePayoutsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var ids = await db.Orders.AsNoTracking().Where(x => x.Type == OrderType.Payout && x.NextActionAt <= now)
            .OrderBy(x => x.NextActionAt).ThenBy(x => x.OrderNo).Select(x => x.OrderNo).Take(20).ToListAsync(cancellationToken);
        foreach (var id in ids)
            await MutateAsync(id, async order =>
            {
                if (order.NextActionAt is null || order.NextActionAt > clock.GetUtcNow()) return;
                var next = order.Status == OrderStatus.Pending
                    ? ScenarioParser.Parse(order.Scenario).Decline ? OrderStatus.Rejected : OrderStatus.Processing
                    : OrderStatus.Succeeded;
                await TransitionAsync(order, next, cancellationToken);
                order.NextActionAt = next == OrderStatus.Processing ? clock.GetUtcNow().AddSeconds(2) : null;
            }, cancellationToken);
    }

    public async Task ExpireOverdueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var ids = await db.Orders.AsNoTracking().Where(x => x.Type == OrderType.PayIn &&
            x.Status == OrderStatus.Pending && x.ExpiresAt <= now).OrderBy(x => x.ExpiresAt).ThenBy(x => x.OrderNo)
            .Select(x => x.OrderNo).Take(100).ToListAsync(cancellationToken);
        foreach (var id in ids)
            await MutateAsync(id, async order =>
            {
                if (order.Status == OrderStatus.Pending && order.ExpiresAt <= clock.GetUtcNow())
                    await TransitionAsync(order, OrderStatus.Expired, cancellationToken);
            }, cancellationToken);
    }

    public async Task ResendAsync(string orderNo, CancellationToken cancellationToken) =>
        await MutateAsync(orderNo, async order => scheduler.Schedule(order,
            await db.Merchants.SingleAsync(x => x.Id == order.MerchantId, cancellationToken), true), cancellationToken);

    private async Task TransitionAsync(PaymentOrder order, OrderStatus status, CancellationToken cancellationToken)
    {
        OrderStateMachine.Transition(order, status, clock.GetUtcNow());
        if (status is OrderStatus.Failed or OrderStatus.Rejected) order.FailureReason = "Simulated decline.";
        if (order.Type == OrderType.Payout && status is OrderStatus.Failed or OrderStatus.Rejected)
        {
            var balance = await db.Balances.SingleAsync(x => x.MerchantId == order.MerchantId && x.Currency == order.Currency, cancellationToken);
            balance.Available += order.Amount;
            balance.Version++;
        }
        var merchant = await db.Merchants.SingleAsync(x => x.Id == order.MerchantId, cancellationToken);
        scheduler.Schedule(order, merchant);
    }

    private async Task<PaymentOrder> MutateAsync(string orderNo, Func<PaymentOrder, Task> mutation, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var order = await db.Orders.SingleOrDefaultAsync(x => x.OrderNo == orderNo, cancellationToken)
                    ?? throw new PaymentException("9999", "Order was not found.", 404);
                await mutation(order);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return order;
            }
            catch (Exception ex) when (attempt < 3 && IsWriteConflict(ex))
            {
                db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), clock, cancellationToken);
            }
        }
    }

    private static bool IsWriteConflict(Exception ex) => ex is DbUpdateConcurrencyException ||
        ex is SqliteException { SqliteErrorCode: 5 or 6 } ||
        ex is DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 5 or 6 or 19 } };

    private static bool Matches(PaymentOrder o, CreateOrderRequest r, OrderType type) =>
        o.Type == type && o.Amount == r.Amount && o.Currency == r.Currency && o.NotifyUrl == r.NotifyUrl &&
        o.ReturnUrl == r.ReturnUrl && o.Payee == r.Payee && (r.Scenario is null || o.Scenario == r.Scenario);

    private static void Validate(CreateOrderRequest r, OrderType type)
    {
        if (string.IsNullOrWhiteSpace(r.MerchantOrderNo) || r.MerchantOrderNo.Length > 100 || r.Amount <= 0 ||
            r.Amount > 1_000_000_000m || decimal.Round(r.Amount, 2) != r.Amount || r.Currency is not ("KRW" or "USD") ||
            !IsHttpUrl(r.NotifyUrl) || (r.ReturnUrl is not null && !IsHttpUrl(r.ReturnUrl)) ||
            (type == OrderType.Payout && (r.Payee is null || string.IsNullOrWhiteSpace(r.Payee.BankCode) ||
                string.IsNullOrWhiteSpace(r.Payee.AccountNo) || string.IsNullOrWhiteSpace(r.Payee.AccountName))))
            throw new PaymentException("9999", "Invalid order fields; use a positive amount with at most two decimals and KRW or USD.", 400);
    }

    public static bool IsHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);
}
