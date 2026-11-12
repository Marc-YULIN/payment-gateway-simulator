using System.Text.Json;
using PaymentSim.Core;
using PaymentSim.Signing;

namespace PaymentSim.Infrastructure;

public sealed class CallbackScheduler(PaymentSimDbContext db, TimeProvider clock)
{
    public void Schedule(PaymentOrder order, Merchant merchant, bool manual = false)
    {
        var scenario = ScenarioParser.Parse(order.Scenario);
        if (scenario.NoCallback && !manual) return;
        if (scenario.OutOfOrder && order.Status == OrderStatus.Processing && !manual) return;
        var snapshots = new List<(OrderStatus Status, int Offset)> { (order.Status, 0) };
        if (!manual && scenario.OutOfOrder && OrderStateMachine.IsTerminal(order.Status))
            snapshots = order.Type == OrderType.PayIn
                ? [(OrderStatus.Failed, 0), (OrderStatus.Paid, 1)]
                : [(order.Status, 0), (OrderStatus.Processing, 1)];
        foreach (var (status, offset) in snapshots)
        {
            var fields = (Dictionary<string, string?>)SignedPayload.FromObject(new
            {
                order.MerchantId, order.MerchantOrderNo, order.OrderNo, order.Amount, order.Currency,
                type = order.Type.ToString(), status = status.ToString(),
                timestamp = clock.GetUtcNow().ToUnixTimeSeconds()
            });
            fields["sign"] = SignatureSchemes.Get(merchant.SignScheme).Sign(fields,
                new SigningCredentials(merchant.SharedSecret, merchant.GatewayPrivateKeyPem));
            for (var i = 0; i < (manual ? 1 : scenario.DuplicateCount); i++)
                db.Outbox.Add(new CallbackOutbox
                {
                    OrderNo = order.OrderNo, StatusSnapshot = status, Payload = JsonSerializer.Serialize(fields),
                    NextAttemptAt = clock.GetUtcNow().AddSeconds((manual ? 0 : scenario.DelaySeconds) + offset),
                    CorruptSignature = !manual && scenario.BadSignature
                });
        }
    }
}
