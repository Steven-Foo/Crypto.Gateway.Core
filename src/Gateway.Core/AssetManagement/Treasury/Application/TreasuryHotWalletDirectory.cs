using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;

/// <summary>
/// Resolves the platform hot withdrawal <b>pool</b> by joining, per chain, the Wallet module's registered
/// <c>HotWithdrawal</c> addresses with each one's per-address KeyManagement signing key — each read through
/// its own Contracts (§4.5). The pool wallets are watch-only children of one platform withdrawal HD wallet, so
/// each resolves its own key <em>by address</em> (<see cref="IPlatformSigningKeyDirectory.FindByAddressAsync"/>).
/// A wallet whose key can't be resolved is skipped (never signed from blindly, §10).
/// </summary>
public sealed class TreasuryHotWalletDirectory(
    IPlatformWalletDirectory wallets,
    IPlatformSigningKeyDirectory signingKeys) : ITreasuryHotWalletDirectory
{
    private const string HotWithdrawalType = "HotWithdrawal";

    public async Task<Result<TreasuryHotWallet>> GetHotWalletAsync(
        Chain chain, CancellationToken cancellationToken = default)
    {
        var pool = await GetHotWalletPoolAsync(chain, cancellationToken);
        return pool.Count == 0
            ? Result.Failure<TreasuryHotWallet>(TreasuryErrors.HotWalletNotConfigured)
            : Result.Success(pool[0]);
    }

    public async Task<IReadOnlyList<TreasuryHotWallet>> GetHotWalletPoolAsync(
        Chain chain, CancellationToken cancellationToken = default)
    {
        var platformWallets = await wallets.GetPlatformWalletsAsync(chain, cancellationToken);

        var pool = new List<TreasuryHotWallet>();
        foreach (var wallet in platformWallets
                     .Where(w => string.Equals(w.WalletType, HotWithdrawalType, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(w => w.Address, StringComparer.Ordinal)) // stable order so GetHotWalletAsync is deterministic
        {
            var signingKey = await signingKeys.FindByAddressAsync(chain, wallet.Address, cancellationToken);
            if (signingKey is not null)
                pool.Add(new TreasuryHotWallet(wallet.WalletId, chain, wallet.Address, signingKey.KeyReference));
        }

        return pool;
    }
}
