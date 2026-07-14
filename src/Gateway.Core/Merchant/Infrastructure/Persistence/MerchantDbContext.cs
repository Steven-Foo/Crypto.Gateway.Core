using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

public sealed class MerchantDbContext(DbContextOptions<MerchantDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "merchant";

    public override string Schema => SchemaName;

    public DbSet<Domain.Merchant> Merchants => Set<Domain.Merchant>();
    public DbSet<MerchantApiCredential> Credentials => Set<MerchantApiCredential>();
    public DbSet<MerchantConfiguration> Configurations => Set<MerchantConfiguration>();
    public DbSet<MerchantAssetPolicy> AssetPolicies => Set<MerchantAssetPolicy>();
    public DbSet<MerchantWebhook> Webhooks => Set<MerchantWebhook>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new MerchantMap());
        modelBuilder.ApplyConfiguration(new MerchantApiCredentialMap());
        modelBuilder.ApplyConfiguration(new MerchantConfigurationMap());
        modelBuilder.ApplyConfiguration(new MerchantAssetPolicyMap());
        modelBuilder.ApplyConfiguration(new MerchantWebhookMap());
    }
}
