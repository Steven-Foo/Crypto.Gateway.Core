using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application;

using WalletEntity = Domain.Wallet;

/// <summary>
/// Provisions a dedicated deposit address for a merchant.
///
/// Two modules, two transactions: KeyManagement derives and commits the key first (via
/// <see cref="IWalletDerivation"/>), then Wallet stores its own row. If the Wallet write fails, the
/// derived key is orphaned — an address nobody points at, which is harmless. What must never happen
/// is a derived index being reused, and custody's atomic allocation already guarantees that.
/// </summary>
public sealed class WalletProvisioningService(
    IWalletRepository repository,
    IWalletDerivation walletDerivation,
    IMerchantDirectory merchantDirectory,
    TimeProvider timeProvider) : IDepositAddressProvisioner
{
    public async Task<Result<ProvisionedDepositAddress>> ProvisionDepositAddressAsync(
        Guid merchantId,
        Chain chain,
        CancellationToken cancellationToken = default)
    {
        // Fail fast: never hand a deposit address to a merchant that cannot transact. This is a
        // Contracts-only read into the Merchant module (§4.5) — no coupling to its internals.
        var merchant = await merchantDirectory.FindByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure<ProvisionedDepositAddress>(WalletErrors.MerchantNotFound);

        if (!merchant.CanTransact)
            return Result.Failure<ProvisionedDepositAddress>(WalletErrors.MerchantCannotTransact);

        // Custody allocates an index and derives the address atomically; we receive only an opaque
        // handle plus the public address.
        var derivation = await walletDerivation.AllocateNextAsync(chain, DerivationPurpose.Deposit, cancellationToken);
        if (derivation.IsFailure)
            return Result.Failure<ProvisionedDepositAddress>(derivation.Error!);

        var derived = derivation.Value;

        // Defensive: the address encoder and the chain we asked for must agree.
        if (derived.Chain != chain)
            return Result.Failure<ProvisionedDepositAddress>(WalletErrors.DerivedChainMismatch);

        var walletResult = WalletEntity.CreateDeposit(
            derived.DerivedKeyId, chain, derived.Address, merchantId, timeProvider: timeProvider);

        if (walletResult.IsFailure)
            return Result.Failure<ProvisionedDepositAddress>(walletResult.Error!);

        var wallet = walletResult.Value;
        repository.Add(wallet);
        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success(new ProvisionedDepositAddress(wallet.Id, chain, wallet.Address));
    }
}
