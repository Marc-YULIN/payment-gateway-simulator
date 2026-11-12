using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentSim.Core;
using PaymentSim.Infrastructure;
using PaymentSim.Signing;

namespace PaymentSim.Tests;

public sealed class DispatcherTests
{
    [Theory]
    [InlineData(CallbackFormat.Form)] [InlineData(CallbackFormat.Json)]
    public async Task DispatchOnceAsync_TwoFailuresThenSuccess_PersistsThreeAttemptsAndDelivers(CallbackFormat format)
    {
        using var handler = new StubHandler((n, _, _) => Task.FromResult(StubHandler.Response(n < 3 ? "fail" : "  SuCcEsS \n")));
        await using var store = await TestStore.CreateAsync(handler, format: format);
        await store.CompletePayInAsync("normal");
        var dispatcher = store.Services.GetRequiredService<CallbackDispatcher>();
        for (var i = 0; i < 3; i++) { await dispatcher.DispatchOnceAsync(CancellationToken.None); store.Clock.Advance(TimeSpan.FromMinutes(10)); }
        using var scope = store.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentSimDbContext>();
        Assert.Equal(OutboxState.Delivered, (await db.Outbox.SingleAsync()).State);
        var attempts = await db.Attempts.OrderBy(x => x.AttemptNo).ToListAsync();
        Assert.Equal(3, attempts.Count);
        Assert.Equal([AttemptOutcome.Rejected, AttemptOutcome.Rejected, AttemptOutcome.Acknowledged], attempts.Select(x => x.Outcome));
        Assert.All(attempts, a => Assert.Equal(200, a.HttpStatus));
    }

    [Theory]
    [InlineData(500, "success")] [InlineData(202, "success")] [InlineData(200, "success!")]
    public async Task DispatchOnceAsync_NonAcknowledgement_RetriesThenDeadLetters(int status, string body)
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Response(body, (HttpStatusCode)status)));
        await using var store = await TestStore.CreateAsync(handler, 3);
        await store.CompletePayInAsync("normal");
        var dispatcher = store.Services.GetRequiredService<CallbackDispatcher>();
        for (var i = 0; i < 4; i++) { await dispatcher.DispatchOnceAsync(CancellationToken.None); store.Clock.Advance(TimeSpan.FromHours(1)); }
        using var scope = store.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentSimDbContext>();
        var outbox = await db.Outbox.SingleAsync();
        Assert.Equal(OutboxState.DeadLetter, outbox.State);
        Assert.Equal(3, outbox.AttemptCount);
        Assert.Equal(3, await db.Attempts.CountAsync());
        Assert.Equal(3, handler.Bodies.Count);
    }

    [Fact]
    public async Task DispatchOnceAsync_TransportFailure_RecordsErrorAndBoundsResponseBody()
    {
        using var handler = new StubHandler((n, _, _) => n == 1 ? throw new HttpRequestException("Synthetic failure") :
            Task.FromResult(StubHandler.Response(new string('x', 2000))));
        await using var store = await TestStore.CreateAsync(handler);
        await store.CompletePayInAsync("normal");
        var dispatcher = store.Services.GetRequiredService<CallbackDispatcher>();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        store.Clock.Advance(TimeSpan.FromMinutes(10));
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        using var scope = store.Services.CreateScope();
        var attempts = await scope.ServiceProvider.GetRequiredService<PaymentSimDbContext>().Attempts.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(AttemptOutcome.Error, attempts[0].Outcome);
        Assert.Null(attempts[0].HttpStatus);
        Assert.Equal(500, attempts[1].ResponseBody!.Length);
    }

    [Fact]
    public async Task DispatchOnceAsync_AcknowledgementPrefixWithHiddenSuffix_IsRejected()
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Response("success" + new string(' ', 600) + "fail")));
        await using var store = await TestStore.CreateAsync(handler);
        await store.CompletePayInAsync("normal");
        await store.Services.GetRequiredService<CallbackDispatcher>().DispatchOnceAsync(CancellationToken.None);
        using var scope = store.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentSimDbContext>();
        Assert.Equal(AttemptOutcome.Rejected, (await db.Attempts.SingleAsync()).Outcome);
        Assert.Equal(OutboxState.Pending, (await db.Outbox.SingleAsync()).State);
    }

    [Fact]
    public async Task DispatchOnceAsync_DuplicateScenario_SendsSameStateTwice()
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Response("success")));
        await using var store = await TestStore.CreateAsync(handler);
        await store.CompletePayInAsync("duplicate:2");
        await store.Services.GetRequiredService<CallbackDispatcher>().DispatchOnceAsync(CancellationToken.None);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Single(handler.Bodies.Distinct());
        using var scope = store.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<PaymentSimDbContext>().Outbox.CountAsync(x => x.State == OutboxState.Delivered));
    }

    [Fact]
    public async Task DispatchOnceAsync_BadSignatureScenario_CorruptsOnlyFirstAttempt()
    {
        var verified = new List<bool>();
        using var handler = new StubHandler((_, body, _) =>
        {
            var fields = body.Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => WebUtility.UrlDecode(x[0]), x => (string?)WebUtility.UrlDecode(x[1]));
            var valid = new HmacSha256Scheme().Verify(fields, fields["sign"]!, new("demo-hmac-secret"));
            verified.Add(valid);
            return Task.FromResult(StubHandler.Response(valid ? "success" : "fail", valid ? HttpStatusCode.OK : HttpStatusCode.BadRequest));
        });
        await using var store = await TestStore.CreateAsync(handler);
        await store.CompletePayInAsync("bad-signature");
        var dispatcher = store.Services.GetRequiredService<CallbackDispatcher>();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        store.Clock.Advance(TimeSpan.FromMinutes(10));
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Equal([false, true], verified);
    }

    [Fact]
    public async Task DispatchOnceAsync_TwoDispatchersSharingSqlite_ClaimsOutboxOnce()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler(async (_, _, ct) => { arrived.TrySetResult(); await release.Task.WaitAsync(ct); return StubHandler.Response("success"); });
        await using var store = await TestStore.CreateAsync(handler);
        await store.CompletePayInAsync("normal");
        var first = store.Services.GetRequiredService<CallbackDispatcher>();
        using var second = ActivatorUtilities.CreateInstance<CallbackDispatcher>(store.Services);
        var active = first.DispatchOnceAsync(CancellationToken.None);
        try
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await second.DispatchOnceAsync(CancellationToken.None);
        }
        finally { release.TrySetResult(); }
        await active;
        Assert.Single(handler.Bodies);
        using var scope = store.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PaymentSimDbContext>().Attempts.CountAsync());
    }

    [Fact]
    public async Task DispatchOnceAsync_AbandonedLease_IsRecovered()
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Response("success")));
        await using var store = await TestStore.CreateAsync(handler);
        await store.CompletePayInAsync("normal");
        using (var scope = store.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentSimDbContext>();
            var item = await db.Outbox.SingleAsync();
            item.State = OutboxState.InFlight;
            item.LeaseUntil = store.Clock.GetUtcNow().AddSeconds(-1);
            item.LeaseToken = "abandoned";
            await db.SaveChangesAsync();
        }
        await store.Services.GetRequiredService<CallbackDispatcher>().DispatchOnceAsync(CancellationToken.None);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Schedule_DelayedOutOfOrderAndNoCallback_PreservesSyntheticSnapshots()
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Response("success")));
        await using var store = await TestStore.CreateAsync(handler);
        var id = await store.CompletePayInAsync("delay:3,out-of-order");
        var absent = await store.CompletePayInAsync("no-callback");
        using var scope = store.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentSimDbContext>();
        var rows = await db.Outbox.Where(x => x.OrderNo == id).OrderBy(x => x.NextAttemptAt).ToListAsync();
        Assert.Equal([OrderStatus.Failed, OrderStatus.Paid], rows.Select(x => x.StatusSnapshot));
        Assert.Equal(store.Clock.GetUtcNow().AddSeconds(3), rows[0].NextAttemptAt);
        Assert.Equal(store.Clock.GetUtcNow().AddSeconds(4), rows[1].NextAttemptAt);
        Assert.False(await db.Outbox.AnyAsync(x => x.OrderNo == absent));
        Assert.All(rows, row => Assert.Equal(row.StatusSnapshot.ToString(), JsonSerializer.Deserialize<Dictionary<string, string?>>(row.Payload)!["status"]));
    }
}
