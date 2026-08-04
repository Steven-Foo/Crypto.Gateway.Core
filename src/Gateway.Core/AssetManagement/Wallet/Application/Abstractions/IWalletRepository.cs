using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application.Abstractions;

using WalletEntity = Domain.Wallet;

public interface IWalletRepository
{
    Task<WalletEntity?> GetByIdAsync(Guid walletId, CancellationToken cancellationToken = default);

    Task<WalletEntity?> GetByDerivedKeyIdAsync(Guid derivedKeyId, CancellationToken cancellationToken = default);

    Task<WalletEntity?> FindByAddressAsync(Chain chain, string address, CancellationToken cancellationToken = default);

    void Add(WalletEntity wallet);

    /// <summary>
    /// Inserts a wallet, saving immediately. Returns <c>false</c> when the unique <c>(Chain, Address)</c>
    /// index rejects it — a concurrent registration for the same address won — so the caller adopts the
    /// existing row instead of failing. Keeps the EF-specific race translation inside Infrastructure (§4.4).
    /// </summary>
    Task<bool> TryAddAsync(WalletEntity wallet, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
