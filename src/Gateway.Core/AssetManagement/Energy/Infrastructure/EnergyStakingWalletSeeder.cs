using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure;

/// <summary>
/// DEV/TESTNET-tier ONLY. On host boot, idempotently provisions the platform staking (energy) wallet from
/// <see cref="EnergyStakingDevOptions.DevStakingWallets"/>: its imported signing key (KeyManagement, purpose
/// <see cref="DerivationPurpose.Energy"/>), its <c>WalletType.Energy</c> Wallet row (which
/// <c>StakingWalletLocator</c> finds), and an <see cref="EnergyPolicy"/> so auto-stake has thresholds. Together
/// these make the wallet the delegation SOURCE the sweep energy gate draws from. All three steps are idempotent
/// (adopt-on-conflict), so a re-run — or a lost create race — is a no-op. Never runs in production (§10): the
/// staking wallet is provisioned through a KMS-backed ops action there. A live delegation additionally needs the
/// wallet funded with (testnet) TRX to freeze for energy.
/// </summary>
public sealed class EnergyStakingWalletSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<EnergyStakingDevOptions> options,
    ILogger<EnergyStakingWalletSeeder> logger) : IHostedService
{
    // Dev defaults (energy units) when a seed omits them.
    private static readonly BigInteger DefaultMinimumEnergy = 100_000;
    private static readonly BigInteger DefaultTargetEnergy = 5_000_000;
    private static readonly BigInteger DefaultStakeThreshold = 1_000_000;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var seed in options.Value.DevStakingWallets)
        {
            if (!Enum.TryParse<Chain>(seed.Chain, ignoreCase: true, out var chain))
            {
                logger.LogWarning("Energy staking-wallet seed skipped: unknown chain '{Chain}'.", seed.Chain);
                continue;
            }

            try
            {
                await SeedAsync(chain, seed, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Energy staking-wallet seeding for {Chain} failed (are the keymgmt/wallet/energy schemas migrated?).", chain);
            }
        }
    }

    private async Task SeedAsync(Chain chain, StakingWalletSeed seed, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var keys = scope.ServiceProvider.GetRequiredService<IPlatformKeyRegistrar>();
        var wallets = scope.ServiceProvider.GetRequiredService<IPlatformWalletRegistrar>();
        var policies = scope.ServiceProvider.GetRequiredService<IEnergyPolicyRepository>();

        // 1. The imported signing key (purpose Energy). The reference points at KeyManagement:DevSecrets.
        var key = await keys.RegisterImportedKeyAsync(
            chain, DerivationPurpose.Energy, seed.Address, seed.SecretReference, "dev staking (energy) wallet", cancellationToken);
        if (key.IsFailure)
        {
            logger.LogWarning("Energy staking-wallet key for {Chain} skipped: {Error}.", chain, key.Error!.Message);
            return;
        }

        // 2. The WalletType.Energy row, so StakingWalletLocator resolves this as the chain's staking wallet.
        var wallet = await wallets.RegisterPlatformWalletAsync(
            key.Value.DerivedKeyId, chain, seed.Address, StakingWalletLocator.EnergyWalletType, "dev staking (energy) wallet", cancellationToken);
        if (wallet.IsFailure)
        {
            logger.LogWarning("Energy staking-wallet row for {Chain} skipped: {Error}.", chain, wallet.Error!.Message);
            return;
        }

        // 3. The auto-stake policy (idempotent — only when none exists for this chain's Energy wallet type).
        await SeedPolicyAsync(policies, chain, seed, cancellationToken);

        logger.LogInformation(
            "Seeded {Chain} staking (energy) wallet {Address} (fund it with TRX to enable real delegation).", chain, seed.Address);
    }

    private async Task SeedPolicyAsync(
        IEnergyPolicyRepository policies, Chain chain, StakingWalletSeed seed, CancellationToken cancellationToken)
    {
        if (await policies.FindAsync(chain, StakingWalletLocator.EnergyWalletType, cancellationToken) is not null)
            return; // already configured

        var policy = EnergyPolicy.Create(
            chain, StakingWalletLocator.EnergyWalletType,
            minimumEnergy: Parse(seed.MinimumEnergy, DefaultMinimumEnergy),
            targetEnergy: Parse(seed.TargetEnergy, DefaultTargetEnergy),
            stakeThreshold: Parse(seed.StakeThreshold, DefaultStakeThreshold),
            rentalThreshold: BigInteger.Zero, // no rental in dev
            enableAutoStake: seed.EnableAutoStake,
            enableAutoRent: false);

        if (policy.IsFailure)
        {
            logger.LogWarning("Energy staking policy for {Chain} skipped: {Error}.", chain, policy.Error!.Message);
            return;
        }

        policies.Add(policy.Value);
        await policies.SaveChangesAsync(cancellationToken);
    }

    private static BigInteger Parse(string? value, BigInteger fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : BigInteger.Parse(value, CultureInfo.InvariantCulture);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
