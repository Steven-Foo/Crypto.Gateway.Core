using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;

/// <summary>
/// Ensures the platform hot withdrawal <b>pool</b> holds a target number of wallets. Each pool wallet is a
/// watch-only child address derived from the one platform withdrawal HD wallet (KeyManagement provisions that
/// wallet on the first derivation) and then registered as a <c>HotWithdrawal</c> row in the Wallet module —
/// both across their own Contracts (§4.5). Idempotent and grow-only: it counts the existing <c>HotWithdrawal</c>
/// wallets and derives+registers just enough to reach <paramref name="targetSize"/>, so reboots don't grow the
/// pool and raising the target adds wallets. A derive/register that fails mid-way leaves the pool short (and
/// possibly an orphaned derived key — an address nobody points at, harmless), and the next run continues.
///
/// This never touches the Ledger: deriving or registering a wallet moves no money (§14). One seed backs the
/// whole pool (Option B) — the children are watch-only, the seed never crosses this boundary (§10).
/// </summary>
public sealed class TreasuryHotWalletProvisioningService(
    IWalletDerivation derivation,
    IPlatformWalletDirectory wallets,
    IPlatformWalletRegistrar walletRegistrar,
    ILogger<TreasuryHotWalletProvisioningService> logger)
{
    private const string HotWithdrawalWalletType = "HotWithdrawal";

    /// <summary>The largest pool a staff action may ask for. Each wallet is a separate float someone has to fund
    /// and watch; twenty covers a busy launch and keeps a mistyped "200" from deriving a pool nobody can fund.</summary>
    public const int MaxPoolSize = 20;

    /// <summary>
    /// The staff action behind <c>POST /ops/treasury/hot-pool/provision</c>: grows the pool to
    /// <paramref name="targetSize"/> (1–<see cref="MaxPoolSize"/>). Grow-only — asking for fewer than exist changes
    /// nothing (a pool wallet may hold funds, so removing one is never a side effect of a number field). Safe to
    /// repeat. In production this is where the platform withdrawal seed is minted and sealed under KMS, on the
    /// first call for a chain.
    /// </summary>
    public async Task<Result<HotPoolProvisioning>> ProvisionAsync(
        Chain chain, int targetSize, CancellationToken cancellationToken = default)
    {
        if (targetSize < 1 || targetSize > MaxPoolSize)
            return Result.Failure<HotPoolProvisioning>(TreasuryErrors.InvalidPoolSize);

        return await GrowAsync(chain, targetSize, cancellationToken);
    }

    /// <summary>The development seeder's entry point (config-driven size, no staff cap).</summary>
    public async Task<Result> EnsurePoolAsync(Chain chain, int targetSize, CancellationToken cancellationToken = default)
    {
        if (targetSize <= 0)
            return Result.Success();

        var grown = await GrowAsync(chain, targetSize, cancellationToken);
        return grown.IsSuccess ? Result.Success() : Result.Failure(grown.Error!);
    }

    private async Task<Result<HotPoolProvisioning>> GrowAsync(Chain chain, int targetSize, CancellationToken cancellationToken)
    {
        var existing = (await wallets.GetPlatformWalletsAsync(chain, cancellationToken))
            .Count(w => string.Equals(w.WalletType, HotWithdrawalWalletType, StringComparison.OrdinalIgnoreCase));

        var created = new List<string>();
        for (var i = existing; i < targetSize; i++)
        {
            // Derive the next child of the platform withdrawal HD wallet (provisions that wallet on the first
            // call; inert where no provisioner is registered → returns a failure, §10).
            var derived = await derivation.AllocateNextAsync(chain, DerivationPurpose.Withdrawal, cancellationToken);
            if (derived.IsFailure)
                return Result.Failure<HotPoolProvisioning>(derived.Error!);

            var registered = await walletRegistrar.RegisterPlatformWalletAsync(
                derived.Value.DerivedKeyId, chain, derived.Value.Address, HotWithdrawalWalletType,
                "Platform hot withdrawal pool wallet", cancellationToken);
            if (registered.IsFailure)
                return Result.Failure<HotPoolProvisioning>(registered.Error!);

            created.Add(derived.Value.Address);
            logger.LogInformation(
                "Registered hot withdrawal pool wallet #{Index} for {Chain}: {Address}.", i, chain, derived.Value.Address);
        }

        return Result.Success(new HotPoolProvisioning(chain, existing, existing + created.Count, created));
    }
}

/// <param name="PreviousSize">Pool wallets that existed before this call.</param>
/// <param name="Size">Pool wallets after it. Equal to <paramref name="PreviousSize"/> when nothing was needed.</param>
/// <param name="CreatedAddresses">The addresses added by this call, in derivation order — the ones to fund.</param>
public sealed record HotPoolProvisioning(Chain Chain, int PreviousSize, int Size, IReadOnlyList<string> CreatedAddresses);
