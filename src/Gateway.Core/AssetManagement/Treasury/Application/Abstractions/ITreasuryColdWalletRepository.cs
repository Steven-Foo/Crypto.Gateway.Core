using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application.Abstractions;

public interface ITreasuryColdWalletRepository
{
    Task<TreasuryColdWallet?> FindByChainAsync(Chain chain, CancellationToken cancellationToken = default);

    Task AddAsync(TreasuryColdWallet wallet, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
