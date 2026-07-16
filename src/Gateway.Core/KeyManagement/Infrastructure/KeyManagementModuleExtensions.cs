using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Derivation;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Signing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

    /// <summary>
    /// DEVELOPMENT / LOCAL ONLY. Wires the in-memory secret provider and an idempotent HD-wallet seeder from
    /// configuration (section <c>KeyManagement</c>: <c>DevWallets</c> + <c>DevSecrets</c>), so a signed
    /// <c>/deposit</c> can provision a deposit address on a fresh clone with no manual seeding. A developer
    /// overrides <c>DevSecrets</c> with any xpub — including a real production branch xpub — via a git-ignored
    /// <c>appsettings.Local.json</c> to reproduce production addresses locally.
    ///
    /// NEVER call this outside the Development branch: the provider reports
    /// <see cref="Domain.SecretProviderKind.InMemoryDevelopment"/>, so it can never back a production wallet
    /// row, and the seeded wallets carry the same kind (§10). <c>DevSecrets</c> is public xpub material only —
    /// never a seed or mnemonic.
    /// </summary>
    public static IServiceCollection AddDevelopmentKeyCustody(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(DevelopmentKeyCustodyOptions.SectionName);
        services.Configure<DevelopmentKeyCustodyOptions>(section);

        var secrets = section.GetSection(nameof(DevelopmentKeyCustodyOptions.DevSecrets))
            .Get<Dictionary<string, string>>() ?? new Dictionary<string, string>();

        // A writable singleton store: pre-seeded from config (including appsettings.Local.json), and written to
        // at runtime by the provisioner when it mints a per-merchant wallet's account xpub. Registered both as
        // the concrete type (for the provisioner) and as the ISecretProvider the derivation path reads.
        var store = new MutableInMemorySecretStore(secrets);
        services.AddSingleton(store);
        services.AddSingleton<ISecretProvider>(store);

        // Per-merchant wallets are created on first deposit, each with its own seed. Production replaces this
        // with a KMS-backed IHdWalletProvisioner behind the same port — never an in-memory seed in prod (§10).
        services.AddSingleton<IHdWalletProvisioner, DevHdWalletProvisioner>();

        // Retained for any platform-wallet dev seeding described in config; per-merchant deposit wallets no
        // longer rely on it (they are provisioned lazily above).
        services.AddHostedService<DevHdWalletSeeder>();

        return services;
    }
}
