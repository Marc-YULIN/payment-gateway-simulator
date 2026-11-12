using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PaymentSim.Core;
using PaymentSim.Infrastructure;

namespace PaymentSim.Tests;

public sealed class ManualTimeProvider : TimeProvider
{
    private long ticks = new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
}

public sealed class StubHandler(Func<int, string, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int calls;
    public ConcurrentQueue<string> Bodies { get; } = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        Bodies.Enqueue(body);
        return await respond(Interlocked.Increment(ref calls), body, cancellationToken);
    }

    public static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };
}

public sealed class TestStore : IAsyncDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "payment-test-" + Guid.NewGuid().ToString("N"));
    public ServiceProvider Services { get; private set; } = null!;
    public ManualTimeProvider Clock { get; } = new();

    public static async Task<TestStore> CreateAsync(StubHandler handler, int maxAttempts = 8, CallbackFormat format = CallbackFormat.Form)
    {
        var store = new TestStore();
        Directory.CreateDirectory(store.directory);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PaymentSim"] = $"Data Source={Path.Combine(store.directory, "test.db")};Pooling=False",
            ["Simulator:BackgroundWorkersEnabled"] = "false", ["Retry:MaxAttempts"] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<TimeProvider>(store.Clock);
        services.AddPaymentSimInfrastructure(configuration);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("callbacks").Returns(_ => new HttpClient(handler, false) { Timeout = TimeSpan.FromSeconds(10) });
        services.AddSingleton(factory);
        store.Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = store.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentSimDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Merchants.Add(new Merchant { Id = "M1001", Name = "Acme Store", SharedSecret = "demo-hmac-secret", CallbackFormat = format });
        db.Balances.Add(new MerchantBalance { MerchantId = "M1001", Currency = "USD", Available = 100m });
        await db.SaveChangesAsync();
        return store;
    }

    public async Task<string> CompletePayInAsync(string scenario)
    {
        using var scope = Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<OrderService>();
        var order = await service.CreatePayInAsync(new("M1001", Guid.NewGuid().ToString("N"), 10m, "USD", "http://receiver.test/notify", Scenario: scenario), CancellationToken.None);
        await service.CompletePayInAsync(order.OrderNo, true, CancellationToken.None);
        return order.OrderNo;
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Directory.Delete(directory, true);
    }
}
