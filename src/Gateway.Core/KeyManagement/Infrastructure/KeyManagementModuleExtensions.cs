using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Derivation;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Signing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure;

public static class KeyManagementModuleExtensions
{
    /// <summary>
    /// Registers custody. Note what is <b>not</b> registered here, by design — the composition root must
    /// supply both, so custody stays decoupled from other modules (§4.5) and from any environment default:
    /// <list type="bullet">
    ///   <item><c>ISecretProvider</c> — a host wires AWS/Azure/Vault/HSM, so production can't silently fall
    ///   back to an in-memory seed. An HD wallet whose provider is unregistered simply fails to derive.</item>
    ///   <item>address encoding (<c>IAddressEncoderFactory</c>) — the Blockchain module owns it; the host
    ///   calls <c>AddBlockchainAddressEncoding()</c>. KeyManagement consumes the port, never the encoders.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddKeyManagementModule(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<KeyManagementDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(
                "__EFMigrationsHistory", KeyManagementDbContext.SchemaName)));

        services.TryAddSingleton(TimeProvider.System);

        // Capability segregation: only schemes that genuinely support watch-only derivation appear.
        // ed25519 is absent by design, not by omission.
        services.AddSingleton<IWatchOnlyKeyDeriver, Bip32Secp256k1KeyDeriver>();
        services.AddSingleton<IKeyDeriverFactory, KeyDeriverFactory>();

        services.AddSingleton<ISecretProviderFactory, SecretProviderFactory>();

        services.AddScoped<IHdWalletRepository, HdWalletRepository>();
        services.AddScoped<IWalletDerivation, WalletDerivationService>();

        return services;
    }

    /// <summary>
    /// Registers the development/test signer, which never touches key material. Production replaces this
    /// behind the same <c>ISigner</c> port with a real KMS/HSM-backed signer — the composition root chooses,
    /// so production can't silently fall back to a fake signer (§10).
    /// </summary>
    public static IServiceCollection AddInMemorySigner(this IServiceCollection services)
    {
        services.TryAddSingleton<ISigner, InMemorySigner>();
        return services;
    }
}
