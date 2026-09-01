using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Persistence;

public sealed class MerchantIdentityDbContext(DbContextOptions<MerchantIdentityDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "merchantidentity";

    public override string Schema => SchemaName;

    public DbSet<MerchantUser> MerchantUsers => Set<MerchantUser>();
    public DbSet<MerchantUserSession> MerchantUserSessions => Set<MerchantUserSession>();
    public DbSet<MerchantRole> MerchantRoles => Set<MerchantRole>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new MerchantRoleMap());
        modelBuilder.ApplyConfiguration(new MerchantUserMap());
        modelBuilder.ApplyConfiguration(new MerchantUserSessionMap());
    }
}
