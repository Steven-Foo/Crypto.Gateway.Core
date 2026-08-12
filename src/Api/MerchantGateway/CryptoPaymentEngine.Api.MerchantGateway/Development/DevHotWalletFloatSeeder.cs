using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.MerchantGateway.Development;

/// <summary>
/// DEV/TESTNET convenience. On boot — <b>after</b> the Treasury pool seeder has derived+registered the hot
/// pool wallets — seeds the <see cref="InMemoryBalanceReader"/> with a healthy float for each registered
/// <c>HotWithdrawal</c> address, so the withdrawal happy-path allocates and sends. The in-memory reader
/// reports zero for every unset address, which the pool allocator would otherwise treat as underfunded and
/// park every dev withdrawal. To exercise the insufficient-balance / no-wallet-available path on purpose, set
/// <c>Withdrawal:DevHotWalletFloatBaseUnits</c> low (or 0).
///
/// <para>No-op when the real TRON balance reader is in play (<c>Chains:Tron:Live=true</c>): the concrete
/// <see cref="InMemoryBalanceReader"/> isn't registered then, so this simply returns. Registered only in the
/// testnet tier, and only after <c>AddDevelopmentTreasuryHotWalletSeed</c>, so the pool exists to read.</para>
/// </summary>
public sealed class DevHotWalletFloatSeeder(
    IServiceScopeFactory scopeFactory,
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<DevHotWalletFloatSeeder> logger) : IHostedService
{
    // 1,000,000 USDT at 6 decimals — a generous default so small dev withdrawals always clear the gate.
    private const string DefaultFloatBaseUnits = "1000000000000";
    private const string HotWithdrawalType = "HotWithdrawal";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Only meaningful with the in-memory reader; absent (null) under live TRON, so this simply no-ops.
        var reader = services.GetService<InMemoryBalanceReader>();
        if (reader is null)
            return;

        var floatText = configuration["Withdrawal:DevHotWalletFloatBaseUnits"];
        if (!BigInteger.TryParse(string.IsNullOrWhiteSpace(floatText) ? DefaultFloatBaseUnits : floatText, out var floatAmount))
            floatAmount = BigInteger.Parse(DefaultFloatBaseUnits);

        await using var scope = scopeFactory.CreateAsyncScope();
        var wallets = scope.ServiceProvider.GetRequiredService<IPlatformWalletDirectory>();
        var assets = scope.ServiceProvider.GetRequiredService<IAssetCatalog>();
        var active = await assets.GetActiveAsync(cancellationToken);

        foreach (var entry in configuration.GetSection("Treasury:HotWalletPool").GetChildren())
        {
            if (!Enum.TryParse<Chain>(entry["Chain"], ignoreCase: true, out var chain))
                continue;

            var hotWallets = (await wallets.GetPlatformWalletsAsync(chain, cancellationToken))
                .Where(w => string.Equals(w.WalletType, HotWithdrawalType, StringComparison.OrdinalIgnoreCase));

            foreach (var wallet in hotWallets)
            foreach (var asset in active.Where(a => a.Chain == chain))
            {
                reader.Set(chain, wallet.Address, asset.AssetId, floatAmount);
                logger.LogInformation(
                    "Dev: seeded hot pool wallet {Address} on {Chain} with {Float} {Symbol} base units for the allocator.",
                    wallet.Address, chain, floatAmount, asset.Symbol);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class DevHotWalletFloatSeederExtensions
{
    /// <summary>Registers the dev hot-pool float seeder. Register it AFTER <c>AddDevelopmentTreasuryHotWalletSeed</c>
    /// so its hosted service starts after the pool has been provisioned.</summary>
    public static IServiceCollection AddDevelopmentHotWalletFloatSeed(this IServiceCollection services) =>
        services.AddHostedService<DevHotWalletFloatSeeder>();
}
