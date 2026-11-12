using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentSim.Core;

namespace PaymentSim.Infrastructure;

public sealed class SeedOptions
{
    [Required] public string M1001Secret { get; set; } = "demo-hmac-secret";
    [Required] public string KeyDirectory { get; set; } = "appdata/keys";
}

public sealed class DemoSeeder(PaymentSimDbContext db, IOptions<SeedOptions> options, ILogger<DemoSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await db.Merchants.AnyAsync(cancellationToken)) return;
        Directory.CreateDirectory(options.Value.KeyDirectory);
        var gatewayPrivate = await LoadOrCreateAsync("gateway-private.pem", cancellationToken);
        var merchantPrivate = await LoadOrCreateAsync("merchant-private.pem", cancellationToken);
        using var gateway = RSA.Create();
        gateway.ImportFromPem(gatewayPrivate);
        using var merchant = RSA.Create();
        merchant.ImportFromPem(merchantPrivate);
        await File.WriteAllTextAsync(Path.Combine(options.Value.KeyDirectory, "gateway-public.pem"), gateway.ExportSubjectPublicKeyInfoPem(), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(options.Value.KeyDirectory, "merchant-public.pem"), merchant.ExportSubjectPublicKeyInfoPem(), cancellationToken);
        db.Merchants.AddRange(
            new Merchant { Id = "M1001", Name = "Acme Store", SharedSecret = options.Value.M1001Secret },
            new Merchant { Id = "M1002", Name = "Sample Store", SignScheme = "MD5", SharedSecret = "demo-md5-secret", CallbackFormat = CallbackFormat.Json },
            new Merchant { Id = "M1003", Name = "Demo Store", SignScheme = "RSA-SHA256", MerchantPublicKeyPem = merchant.ExportSubjectPublicKeyInfoPem(),
                GatewayPrivateKeyPem = gatewayPrivate, GatewayPublicKeyPem = gateway.ExportSubjectPublicKeyInfoPem() });
        foreach (var id in new[] { "M1001", "M1002", "M1003" })
            foreach (var currency in new[] { "KRW", "USD" })
                db.Balances.Add(new MerchantBalance { MerchantId = id, Currency = currency, Available = 100000m });
        await db.SaveChangesAsync(cancellationToken);
        var merchantKeyPath = Path.GetFullPath(Path.Combine(options.Value.KeyDirectory, "merchant-private.pem"));
        if (logger.IsEnabled(LogLevel.Information))
            SimulatorLog.KeyPath(logger, merchantKeyPath);
    }

    private async Task<string> LoadOrCreateAsync(string fileName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(options.Value.KeyDirectory, fileName);
        if (File.Exists(path)) return await File.ReadAllTextAsync(path, cancellationToken);
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        await File.WriteAllTextAsync(path, pem, cancellationToken);
        return pem;
    }
}
