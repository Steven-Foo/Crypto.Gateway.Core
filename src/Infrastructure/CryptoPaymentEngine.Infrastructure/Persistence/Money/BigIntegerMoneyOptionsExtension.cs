using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPaymentEngine.Infrastructure.Persistence.Money;

/// <summary>
/// Registers <see cref="BigIntegerTypeMappingPlugin"/> through EF's supported type-mapping plugin
/// extension point — the same mechanism the spatial providers use. Avoids
/// <c>UseInternalServiceProvider</c>, so normal <c>AddDbContext</c> DI keeps working.
/// </summary>
public sealed class BigIntegerMoneyOptionsExtension : IDbContextOptionsExtension
{
    public DbContextOptionsExtensionInfo Info => field ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) =>
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRelationalTypeMappingSourcePlugin, BigIntegerTypeMappingPlugin>());

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using BigInteger money mapping ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
            other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["Money:BigInteger"] = "1";
    }
}

public static class BigIntegerMoneyDbContextOptionsExtensions
{
    /// <summary>
    /// Enables exact <see cref="System.Numerics.BigInteger"/> ↔ <c>decimal(38,0)</c> mapping.
    /// Must be called on every module's DbContext that stores money.
    /// </summary>
    public static DbContextOptionsBuilder UseBigIntegerMoney(this DbContextOptionsBuilder builder)
    {
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new BigIntegerMoneyOptionsExtension());
        return builder;
    }

    /// <inheritdoc cref="UseBigIntegerMoney(DbContextOptionsBuilder)"/>
    public static DbContextOptionsBuilder<TContext> UseBigIntegerMoney<TContext>(
        this DbContextOptionsBuilder<TContext> builder)
        where TContext : DbContext
    {
        UseBigIntegerMoney((DbContextOptionsBuilder)builder);
        return builder;
    }
}
