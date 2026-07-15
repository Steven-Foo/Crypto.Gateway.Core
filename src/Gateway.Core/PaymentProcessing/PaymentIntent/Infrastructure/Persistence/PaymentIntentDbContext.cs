using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using PaymentIntentEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain.PaymentIntent;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Persistence;

public sealed class PaymentIntentDbContext(DbContextOptions<PaymentIntentDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "paymentintent";

    public override string Schema => SchemaName;

    public DbSet<PaymentIntentEntity> PaymentIntents => Set<PaymentIntentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new PaymentIntentMap());
    }
}
