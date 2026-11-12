using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PaymentSim.Core;

namespace PaymentSim.Infrastructure;

public sealed class PaymentSimDbContext(DbContextOptions<PaymentSimDbContext> options) : DbContext(options)
{
    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<MerchantBalance> Balances => Set<MerchantBalance>();
    public DbSet<PaymentOrder> Orders => Set<PaymentOrder>();
    public DbSet<CallbackOutbox> Outbox => Set<CallbackOutbox>();
    public DbSet<CallbackAttempt> Attempts => Set<CallbackAttempt>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<decimal>().HaveConversion<MoneyConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Merchant>().HasKey(x => x.Id);
        modelBuilder.Entity<MerchantBalance>().HasKey(x => new { x.MerchantId, x.Currency });
        modelBuilder.Entity<MerchantBalance>().Property(x => x.Version).IsConcurrencyToken();
        modelBuilder.Entity<MerchantBalance>().HasOne<Merchant>().WithMany().HasForeignKey(x => x.MerchantId);
        modelBuilder.Entity<PaymentOrder>().HasKey(x => x.OrderNo);
        modelBuilder.Entity<PaymentOrder>().HasIndex(x => new { x.MerchantId, x.MerchantOrderNo }).IsUnique();
        modelBuilder.Entity<PaymentOrder>().Property(x => x.Version).IsConcurrencyToken();
        modelBuilder.Entity<PaymentOrder>().HasOne<Merchant>().WithMany().HasForeignKey(x => x.MerchantId);
        modelBuilder.Entity<PaymentOrder>().OwnsOne(x => x.Payee);
        modelBuilder.Entity<CallbackOutbox>().HasIndex(x => new { x.State, x.NextAttemptAt });
        modelBuilder.Entity<CallbackOutbox>().HasOne<PaymentOrder>().WithMany().HasForeignKey(x => x.OrderNo);
        modelBuilder.Entity<CallbackAttempt>().HasIndex(x => new { x.OutboxId, x.AttemptNo }).IsUnique();
        modelBuilder.Entity<CallbackAttempt>().HasOne<CallbackOutbox>().WithMany().HasForeignKey(x => x.OutboxId);
    }
}

public sealed class MoneyConverter() : ValueConverter<decimal, long>(v => checked((long)(v * 10000m)), v => v / 10000m);
public sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
