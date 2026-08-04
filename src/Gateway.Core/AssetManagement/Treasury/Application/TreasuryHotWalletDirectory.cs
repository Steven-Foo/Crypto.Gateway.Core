using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;

/// <summary>
/// Resolves the platform hot withdrawal wallet by joining, on chain, the Wallet module's registered
/// <c>HotWithdrawal</c> address with the KeyManagement module's <c>Withdrawal</c>-purpose signing key —
/// each read through its own Contracts (§4.5). Refuses if zero or more than one hot wallet is registered
/// (multi-wallet selection is out of scope for this cut): a withdrawal must never be signed from an
/// arbitrary wallet.
/// </summary>
public sealed class TreasuryHotWalletDirectory(
    IPlatformWalletDirectory wallets,
    IPlatformSigningKeyDirectory signingKeys) : ITreasuryHotWalletDirectory
{
    private const string HotWithdrawalType = "HotWithdrawal";

    public async Task<Result<TreasuryHotWallet>> GetHotWalletAsync(
        Chain chain, CancellationToken cancellationToken = default)
    {
        var platformWallets = await wallets.GetPlatformWalletsAsync(chain, cancellationToken);
        var hotWallets = platformWallets
            .Where(w => string.Equals(w.WalletType, HotWithdrawalType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (hotWallets.Count == 0)
            return Result.Failure<TreasuryHotWallet>(TreasuryErrors.HotWalletNotConfigured);

        if (hotWallets.Count > 1)
            return Result.Failure<TreasuryHotWallet>(TreasuryErrors.HotWalletAmbiguous);

        var signingKey = await signingKeys.FindActiveAsync(chain, DerivationPurpose.Withdrawal, cancellationToken);
        if (signingKey is null)
            return Result.Failure<TreasuryHotWallet>(TreasuryErrors.SigningKeyMissing);

        return Result.Success(new TreasuryHotWallet(chain, hotWallets[0].Address, signingKey.KeyReference));
    }
}
