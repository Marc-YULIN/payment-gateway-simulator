using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaymentSim.Core;

namespace PaymentSim.Infrastructure;

public sealed class SimulatorOptions
{
    [Required] public string AdminKey { get; set; } = "dev-admin-key";
    [Required, Url] public string PublicBaseUrl { get; set; } = "http://localhost:5102";
    public bool BackgroundWorkersEnabled { get; set; } = true;
}

public static class DependencyInjection
{
    public static IServiceCollection AddPaymentSimInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RetryOptions>().BindConfiguration("Retry").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<SeedOptions>().BindConfiguration("Seed").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<SimulatorOptions>().BindConfiguration("Simulator").ValidateDataAnnotations().ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddDbContext<PaymentSimDbContext>(o => o.UseSqlite(configuration.GetConnectionString("PaymentSim") ??
            "Data Source=appdata/payment-gateway-simulator.db"));
        services.AddSingleton<RequestAuthenticator>();
        services.AddScoped<CallbackScheduler>();
        services.AddScoped<OrderService>();
        services.AddScoped<DemoSeeder>();
        services.AddHttpClient("callbacks", c => c.Timeout = TimeSpan.FromSeconds(10)).RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton<CallbackDispatcher>();
        if (configuration.GetValue("Simulator:BackgroundWorkersEnabled", true))
        {
            services.AddHostedService(sp => sp.GetRequiredService<CallbackDispatcher>());
            services.AddHostedService<OrderWorker>();
        }
        return services;
    }
}

public sealed class OrderWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<OrderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextExpiry = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<OrderService>();
                await service.ProcessDuePayoutsAsync(stoppingToken);
                if (clock.GetUtcNow() >= nextExpiry)
                {
                    await service.ExpireOverdueAsync(stoppingToken);
                    nextExpiry = clock.GetUtcNow().AddSeconds(30);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { SimulatorLog.PollFailed(logger, "Order", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
        }
    }
}
