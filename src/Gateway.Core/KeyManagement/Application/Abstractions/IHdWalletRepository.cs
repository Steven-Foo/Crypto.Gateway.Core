using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;

public interface IHdWalletRepository
{
    Task<HdWallet?> FindActiveAsync(Chain chain, HdWalletPurpose purpose, CancellationToken cancellationToken = default);

    Task<HdWallet?> FindByIdAsync(Guid hdWalletId, CancellationToken cancellationToken = default);

    Task<DerivedKey?> FindDerivedKeyAsync(Guid derivedKeyId, CancellationToken cancellationToken = default);

    void Add(HdWallet hdWallet);

    /// <summary>
    /// Consumes exactly one derivation index, atomically, and returns it.
    ///
    /// This is a single <c>UPDATE … SET NextDerivationIndex = NextDerivationIndex + 1
    /// OUTPUT deleted.NextDerivationIndex</c>, not a read-modify-write: two concurrent callers must
    /// never receive the same index, because a reused index gives two merchants the same deposit
    /// address and silently misattributes every payment to it.
    ///
    /// Returns <see cref="KeyManagementErrors.PoolExhausted"/> when the index space is spent, and
    /// <see cref="KeyManagementErrors.NotActive"/> when the wallet is no longer active.
    /// </summary>
    Task<Result<long>> AllocateNextIndexAsync(Guid hdWalletId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="operation"/> inside a database transaction. Index allocation and the
    /// <see cref="DerivedKey"/> insert must commit together: if the insert fails, the increment is
    /// rolled back and the index is handed out again to the next caller. That gives us neither a
    /// gap nor — critically — a reuse of an index whose address was already handed out.
    /// </summary>
    Task<Result<T>> InTransactionAsync<T>(
        Func<CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken = default);

    void AddDerivedKey(DerivedKey derivedKey);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
