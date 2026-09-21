using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application.Abstractions;

public interface ITreasuryColdWalletRepository
{
    /// <summary>The wallet currently receiving sweeps for a (chain, kind), or null when none is designated.</summary>
    Task<TreasuryColdWallet?> FindActiveAsync(
        Chain chain, ColdWalletKind kind, CancellationToken cancellationToken = default);

    Task<TreasuryColdWallet?> FindByIdAsync(Guid walletId, CancellationToken cancellationToken = default);

    Task<TreasuryColdWallet?> FindByAddressAsync(
        Chain chain, string address, CancellationToken cancellationToken = default);

    /// <summary>Every registered wallet, retired included. The custody audit and the staff list both need
    /// the retired ones — see <see cref="ColdWalletStatus.Retired"/>.</summary>
    Task<IReadOnlyList<TreasuryColdWallet>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TreasuryColdWallet>> ListForChainAsync(
        Chain chain, CancellationToken cancellationToken = default);

    Task AddAsync(TreasuryColdWallet wallet, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs several saves as one unit — designating a destination retires the wallet it replaces,
    /// and the two must not be half-applied.</summary>
    Task<T> InTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);
}
