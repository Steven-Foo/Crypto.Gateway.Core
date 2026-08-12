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

    public async Task<Result> EnsurePoolAsync(Chain chain, int targetSize, CancellationToken cancellationToken = default)
    {
        if (targetSize <= 0)
            return Result.Success();

        var existing = (await wallets.GetPlatformWalletsAsync(chain, cancellationToken))
            .Count(w => string.Equals(w.WalletType, HotWithdrawalWalletType, StringComparison.OrdinalIgnoreCase));

        for (var i = existing; i < targetSize; i++)
        {
            // Derive the next child of the platform withdrawal HD wallet (provisions that wallet on the first
            // call; inert in production until a KMS-backed provisioner lands → returns a failure, §10).
            var derived = await derivation.AllocateNextAsync(chain, DerivationPurpose.Withdrawal, cancellationToken);
            if (derived.IsFailure)
                return Result.Failure(derived.Error!);

            var registered = await walletRegistrar.RegisterPlatformWalletAsync(
                derived.Value.DerivedKeyId, chain, derived.Value.Address, HotWithdrawalWalletType,
                "Platform hot withdrawal pool wallet", cancellationToken);
            if (registered.IsFailure)
                return Result.Failure(registered.Error!);

            logger.LogInformation(
                "Registered hot withdrawal pool wallet #{Index} for {Chain}: {Address}.", i, chain, derived.Value.Address);
        }

        return Result.Success();
    }
}
