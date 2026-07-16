using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;

/// <summary>
/// The Wallet module's public read model. The Deposit module will use <see cref="FindByAddressAsync"/>
/// to answer "a payment arrived at this address — whose is it?" without touching Wallet internals.
/// </summary>
public sealed record WalletOwnership(
    Guid WalletId,
    Guid DerivedKeyId,
    Chain Chain,
    string Address,
    string WalletType,
    Guid? MerchantId,
    bool IsActive);

/// <summary>One of a merchant's active deposit wallets, ordered by deposit activity (see
/// <c>Wallet.DepositsReceivedCount</c>) — highest first, so callers picking a reusable address get the one
/// closest to needing a sweep, without any module holding a money amount outside the Ledger.</summary>
public sealed record AvailableWallet(Guid WalletId, string Address);

public interface IWalletDirectory
{
    Task<WalletOwnership?> FindByAddressAsync(Chain chain, string address, CancellationToken cancellationToken = default);

    Task<WalletOwnership?> FindByIdAsync(Guid walletId, CancellationToken cancellationToken = default);

    /// <summary>All of a merchant's active Deposit wallets on a chain, ordered by deposit activity
    /// descending (see <see cref="AvailableWallet"/>). Includes wallets that have never had a PaymentIntent
    /// created against them yet — e.g. a freshly pre-provisioned pool.</summary>
    Task<IReadOnlyList<AvailableWallet>> ListAssignedWalletsAsync(
        Guid merchantId, Chain chain, CancellationToken cancellationToken = default);
}
