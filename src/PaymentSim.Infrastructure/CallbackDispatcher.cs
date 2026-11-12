using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentSim.Core;

namespace PaymentSim.Infrastructure;

public sealed class CallbackDispatcher(IServiceScopeFactory scopes, IHttpClientFactory clients, TimeProvider clock,
    IOptions<RetryOptions> retry, ILogger<CallbackDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { SimulatorLog.PollFailed(logger, "Callback", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
        }
    }

    public async Task DispatchOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentSimDbContext>();
        var now = clock.GetUtcNow();
        var ids = await db.Outbox.AsNoTracking().Where(x =>
                (x.State == OutboxState.Pending && x.NextAttemptAt <= now) ||
                (x.State == OutboxState.InFlight && x.LeaseUntil < now))
            .OrderBy(x => x.NextAttemptAt).ThenBy(x => x.Id).Select(x => x.Id).Take(20).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            now = clock.GetUtcNow();
            var leaseToken = Guid.NewGuid().ToString("N");
            var claimed = await db.Outbox.Where(x => x.Id == id &&
                    ((x.State == OutboxState.Pending && x.NextAttemptAt <= now) ||
                     (x.State == OutboxState.InFlight && x.LeaseUntil < now)))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, OutboxState.InFlight)
                    .SetProperty(x => x.LeaseUntil, now.AddSeconds(30)).SetProperty(x => x.LeaseToken, leaseToken)
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1), cancellationToken);
            if (claimed == 0) continue;
            var item = await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
            var order = await db.Orders.AsNoTracking().SingleAsync(x => x.OrderNo == item.OrderNo, cancellationToken);
            var merchant = await db.Merchants.AsNoTracking().SingleAsync(x => x.Id == order.MerchantId, cancellationToken);
            var fields = JsonSerializer.Deserialize<Dictionary<string, string?>>(item.Payload)!;
            if (item.CorruptSignature && item.AttemptCount == 1) fields["sign"] = "invalid-signature";
            using HttpContent content = merchant.CallbackFormat == CallbackFormat.Form
                ? new FormUrlEncodedContent(fields.Select(x => new KeyValuePair<string, string>(x.Key, x.Value ?? "")))
                : JsonContent.Create(fields);
            var body = await content.ReadAsStringAsync(cancellationToken);
            var attempt = new CallbackAttempt
            {
                OutboxId = item.Id, OrderNo = item.OrderNo, AttemptNo = item.AttemptCount,
                RequestBody = body, SentAt = clock.GetUtcNow()
            };
            var timer = Stopwatch.StartNew();
            try
            {
                using var client = clients.CreateClient("callbacks");
                using var response = await client.PostAsync(order.NotifyUrl, content, cancellationToken);
                attempt.HttpStatus = (int)response.StatusCode;
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                attempt.ResponseBody = responseBody[..Math.Min(500, responseBody.Length)];
                attempt.Outcome = response.StatusCode == HttpStatusCode.OK &&
                    responseBody.Trim().Equals("success", StringComparison.OrdinalIgnoreCase)
                    ? AttemptOutcome.Acknowledged : AttemptOutcome.Rejected;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempt.Outcome = AttemptOutcome.Error;
                attempt.Error = ex.GetType().Name;
            }
            attempt.DurationMs = timer.ElapsedMilliseconds;
            var state = attempt.Outcome == AttemptOutcome.Acknowledged ? OutboxState.Delivered :
                item.AttemptCount >= retry.Value.MaxAttempts ? OutboxState.DeadLetter : OutboxState.Pending;
            var next = clock.GetUtcNow().Add(RetryPolicy.NextDelay(item.AttemptCount, retry.Value, Random.Shared));
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var owned = await db.Outbox.Where(x => x.Id == item.Id && x.State == OutboxState.InFlight && x.LeaseToken == leaseToken)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, state).SetProperty(x => x.NextAttemptAt, next)
                    .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null).SetProperty(x => x.LeaseToken, (string?)null), cancellationToken);
            if (owned == 1)
            {
                db.Attempts.Add(attempt);
                await db.SaveChangesAsync(cancellationToken);
                if (logger.IsEnabled(LogLevel.Information))
                    SimulatorLog.CallbackResult(logger, item.Id, item.AttemptCount, attempt.Outcome);
            }
            await transaction.CommitAsync(cancellationToken);
        }
    }
}
