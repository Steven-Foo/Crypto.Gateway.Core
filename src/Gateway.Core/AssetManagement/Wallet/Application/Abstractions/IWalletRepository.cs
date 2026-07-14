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

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
