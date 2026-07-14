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

public interface IWalletDirectory
{
    Task<WalletOwnership?> FindByAddressAsync(Chain chain, string address, CancellationToken cancellationToken = default);

    Task<WalletOwnership?> FindByIdAsync(Guid walletId, CancellationToken cancellationToken = default);
}
