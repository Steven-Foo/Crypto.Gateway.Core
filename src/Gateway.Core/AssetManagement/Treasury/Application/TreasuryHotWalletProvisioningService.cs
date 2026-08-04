using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;

/// <summary>
/// Registers the platform's single hot withdrawal wallet across two modules, in order: KeyManagement first
/// (the imported signing key + its <c>DerivedKey</c> record), then Wallet (the <c>HotWithdrawal</c> address
/// row that quotes the key's <c>DerivedKeyId</c>). Order matters the same way it does for deposit
/// provisioning: an orphaned key (KeyManagement committed, Wallet not) is harmless — an address nobody
/// points at — whereas a wallet row referencing a key that was never committed would be broken. Both steps
/// are individually idempotent, so the whole operation is safe to re-run (host reboots, retries).
///
/// This never touches the Ledger: registering a wallet moves no money (§14).
/// </summary>
public sealed class TreasuryHotWalletProvisioningService(
    IPlatformKeyRegistrar keyRegistrar,
    IPlatformWalletRegistrar walletRegistrar,
    ILogger<TreasuryHotWalletProvisioningService> logger)
{
    private const string HotWithdrawalWalletType = "HotWithdrawal";

    public async Task<Result> ProvisionHotWalletAsync(
        Chain chain,
        string address,
        string secretReference,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        var keyResult = await keyRegistrar.RegisterImportedKeyAsync(
            chain, DerivationPurpose.Withdrawal, address, secretReference, description, cancellationToken);
        if (keyResult.IsFailure)
            return Result.Failure(keyResult.Error!);

        var key = keyResult.Value;

        var walletResult = await walletRegistrar.RegisterPlatformWalletAsync(
            key.DerivedKeyId, chain, key.Address, HotWithdrawalWalletType, description, cancellationToken);
        if (walletResult.IsFailure)
            return Result.Failure(walletResult.Error!);

        logger.LogInformation(
            "Registered platform hot withdrawal wallet for {Chain}: address {Address}.", chain, key.Address);

        return Result.Success();
    }
}
