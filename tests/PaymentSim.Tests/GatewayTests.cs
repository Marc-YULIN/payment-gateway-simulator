using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaymentSim.Core;
using PaymentSim.Infrastructure;
using PaymentSim.Signing;

namespace PaymentSim.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "payment-api-" + Guid.NewGuid().ToString("N"));
    public ManualTimeProvider Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(directory);
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:PaymentSim", $"Data Source={Path.Combine(directory, "test.db")};Pooling=False");
        builder.UseSetting("Seed:KeyDirectory", Path.Combine(directory, "keys"));
        builder.UseSetting("Simulator:BackgroundWorkersEnabled", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddHttpClient("callbacks").ConfigurePrimaryHttpMessageHandler(() =>
                new StubHandler((_, _, _) => Task.FromResult(StubHandler.Response("success"))));
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    public Dictionary<string, string?> Fields(string merchantOrderNo = "test-1", decimal amount = 10m, string? scenario = null)
    {
        var fields = (Dictionary<string, string?>)SignedPayload.FromObject(new
        {
            merchantId = "M1001", merchantOrderNo, amount, currency = "USD", notifyUrl = "http://receiver.test/notify",
            scenario, timestamp = Clock.GetUtcNow().ToUnixTimeSeconds(), nonce = Guid.NewGuid().ToString("N")
        });
        Sign(fields);
        return fields;
    }

    public static void Sign(Dictionary<string, string?> fields) => fields["sign"] = new HmacSha256Scheme().Sign(fields, new("demo-hmac-secret"));
}

public sealed class GatewayTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PayIn_ValidSignedRequest_ReturnsVerifiableResponse(bool form)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var fields = factory.Fields();
        using HttpContent content = form ? new FormUrlEncodedContent(fields.Select(x => new KeyValuePair<string, string>(x.Key, x.Value ?? ""))) : JsonContent.Create(fields);
        using var response = await client.PostAsync("/gateway/payin", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Dictionary<string, string?>>();
        Assert.True(new HmacSha256Scheme().Verify(result!, result!["sign"]!, new("demo-hmac-secret")));
        Assert.Equal("0000", result["code"]);
        Assert.Matches("^P[0-9]{8}[A-F0-9]{8}$", result["orderNo"]!);
        using var cashier = await client.GetAsync(new Uri(result["cashierUrl"]!).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, cashier.StatusCode);
        Assert.Contains("Pay successfully", await cashier.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PayIn_TamperedSignature_Returns401AndCode1001()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var fields = factory.Fields(); fields["amount"] = "99.00";
        using var response = await client.PostAsJsonAsync("/gateway/payin", fields);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertCodeAsync(response, "1001");
    }

    [Fact]
    public async Task PayIn_ReplayedNonce_Returns409AndCode1003()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var fields = factory.Fields();
        using var first = await client.PostAsJsonAsync("/gateway/payin", fields);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = await client.PostAsJsonAsync("/gateway/payin", fields);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await AssertCodeAsync(second, "1003");
    }

    [Fact]
    public async Task PayIn_StaleTimestamp_Returns401AndCode1002()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var fields = factory.Fields();
        fields["timestamp"] = (factory.Clock.GetUtcNow().ToUnixTimeSeconds() - 301).ToString(CultureInfo.InvariantCulture); ApiFactory.Sign(fields);
        using var response = await client.PostAsJsonAsync("/gateway/payin", fields);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertCodeAsync(response, "1002");
    }

    [Fact]
    public async Task PayIn_SameOrderIdenticalOrChangedParameters_IsIdempotentOrConflicts()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var first = await client.PostAsJsonAsync("/gateway/payin", factory.Fields());
        using var second = await client.PostAsJsonAsync("/gateway/payin", factory.Fields());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<Dictionary<string, string?>>())!["orderNo"],
            (await second.Content.ReadFromJsonAsync<Dictionary<string, string?>>())!["orderNo"]);
        using var changed = await client.PostAsJsonAsync("/gateway/payin", factory.Fields(amount: 20m));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        await AssertCodeAsync(changed, "2001");
    }

    [Fact]
    public async Task Payout_InsufficientBalance_Returns422AndCode3001()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var fields = PayoutFields(factory, 100001m);
        using var response = await client.PostAsJsonAsync("/gateway/payout", fields);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertCodeAsync(response, "3001");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Payout_FailedOrRejected_ReturnsReservedFundsExactlyOnce(bool decline)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/gateway/payout", PayoutFields(factory, 25m, decline ? "decline" : "normal"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<Dictionary<string, string?>>())!["orderNo"]!;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentSimDbContext>();
            Assert.Equal(99975m, (await db.Balances.SingleAsync(x => x.MerchantId == "M1001" && x.Currency == "USD")).Available);
            factory.Clock.Advance(TimeSpan.FromSeconds(2));
            var service = scope.ServiceProvider.GetRequiredService<OrderService>();
            await service.ProcessDuePayoutsAsync(CancellationToken.None);
            if (!decline) await service.CompletePayoutAsync(id, false, CancellationToken.None);
            await service.ProcessDuePayoutsAsync(CancellationToken.None);
        }
        using var check = factory.Services.CreateScope();
        var verify = check.ServiceProvider.GetRequiredService<PaymentSimDbContext>();
        Assert.Equal(100000m, (await verify.Balances.SingleAsync(x => x.MerchantId == "M1001" && x.Currency == "USD")).Available);
        Assert.Equal(decline ? OrderStatus.Rejected : OrderStatus.Failed, (await verify.Orders.SingleAsync()).Status);
    }

    [Fact]
    public async Task Admin_MissingKey_IsDeniedAndMerchantListOmitsSecrets()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var denied = await client.GetAsync("/admin/api/merchants");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var page = await client.GetAsync("/admin/callbacks");
        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
        client.DefaultRequestHeaders.Add("X-Admin-Key", "dev-admin-key");
        using var allowed = await client.GetAsync("/admin/api/merchants");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var json = await allowed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("demo-hmac-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_SignedRequest_ReturnsCurrentStatusAndSignature()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/gateway/payin", factory.Fields());
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var fields = factory.Fields();
        using var response = await client.PostAsJsonAsync("/gateway/query", fields);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Dictionary<string, string?>>();
        Assert.Equal("Pending", result!["status"]);
        Assert.True(new HmacSha256Scheme().Verify(result, result["sign"]!, new("demo-hmac-secret")));
    }

    [Fact]
    public async Task ExpireOverdueAsync_OverduePayIn_ExpiresAndEnqueuesOnce()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/gateway/payin", factory.Fields());
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<OrderService>();
        await service.ExpireOverdueAsync(CancellationToken.None);
        await service.ExpireOverdueAsync(CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<PaymentSimDbContext>();
        Assert.Equal(OrderStatus.Expired, (await db.Orders.SingleAsync()).Status);
        Assert.Equal(1, await db.Outbox.CountAsync());
    }

    private static Dictionary<string, string?> PayoutFields(ApiFactory factory, decimal amount, string? scenario = null)
    {
        var fields = factory.Fields(amount: amount, scenario: scenario);
        fields["bankCode"] = "DEMO"; fields["accountNo"] = "00001234"; fields["accountName"] = "Sample Person";
        ApiFactory.Sign(fields);
        return fields;
    }

    private static async Task AssertCodeAsync(HttpResponseMessage response, string expected)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, document.RootElement.GetProperty("code").GetString());
    }
}
