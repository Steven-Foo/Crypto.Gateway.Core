using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;

/// <summary>Outcome of racing to create a merchant's first HD wallet.</summary>
public enum HdWalletAddOutcome
{
    /// <summary>This caller's wallet was inserted.</summary>
    Added = 1,

    /// <summary>Another caller already created the merchant's active wallet (unique index rejected the insert).</summary>
    DuplicateActive = 2,
}

/// <summary>
/// The pieces needed to resolve a deposit address's signing key: the derived key + its index, plus the owning
/// HD wallet's secret reference — resolved in one join. The secret reference points at the seed; the index
/// selects the child. Never key material (§10).
/// </summary>
public sealed record DepositSigningKeyInfo(Guid DerivedKeyId, Chain Chain, string SecretReference, long DerivationIndex);

public interface IHdWalletRepository
{
    /// <summary>The single active <em>platform</em> wallet for this chain and purpose (MerchantId is null).</summary>
    Task<HdWallet?> FindActiveAsync(Chain chain, HdWalletPurpose purpose, CancellationToken cancellationToken = default);

    /// <summary>The merchant's own active wallet for this chain and purpose, or null if not yet provisioned.</summary>
    Task<HdWallet?> FindActiveForMerchantAsync(Guid merchantId, Chain chain, HdWalletPurpose purpose, CancellationToken cancellationToken = default);

    Task<HdWallet?> FindByIdAsync(Guid hdWalletId, CancellationToken cancellationToken = default);

    Task<DerivedKey?> FindDerivedKeyAsync(Guid derivedKeyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active <em>deposit</em> signing key for an address (join of <c>DerivedKey</c> ↔ its active,
    /// deposit-purpose <c>HdWallet</c>), or null when none matches. Used by Sweep to sign FROM a deposit
    /// address — returns a reference + index, never key material (§10).
    /// </summary>
    Task<DepositSigningKeyInfo?> FindDepositSigningKeyByAddressAsync(
        Chain chain, string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active <em>platform withdrawal</em> signing key for an address (join of <c>DerivedKey</c> ↔ its
    /// active, withdrawal-purpose, platform (MerchantId null) <c>HdWallet</c>), or null when none matches. Used
    /// to resolve which of the hot pool's child keys signs a payout — a reference + index, never key material
    /// (§10). Reuses <see cref="DepositSigningKeyInfo"/> (a generic id/secret/index tuple).
    /// </summary>
    Task<DepositSigningKeyInfo?> FindPlatformWithdrawalSigningKeyByAddressAsync(
        Chain chain, string address, CancellationToken cancellationToken = default);

    /// <summary>The derived key at <paramref name="index"/> of the given HD wallet, or null. Used by the
    /// platform-key registrar to resolve an already-registered imported wallet's address idempotently.</summary>
    Task<DerivedKey?> FindDerivedKeyForWalletAsync(
        Guid hdWalletId, long index, CancellationToken cancellationToken = default);

    void Add(HdWallet hdWallet);

    /// <summary>
    /// Inserts a merchant's newly-provisioned wallet, saving immediately. Returns
    /// <see cref="HdWalletAddOutcome.DuplicateActive"/> when the unique <c>(MerchantId, Chain, Purpose)</c>
    /// index rejects it — a concurrent first deposit won the race — so the caller adopts the winner instead
    /// of minting a second seed. Keeps the EF-specific race translation inside Infrastructure (§4.4).
    /// </summary>
    Task<HdWalletAddOutcome> TryAddActiveAsync(HdWallet hdWallet, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts an imported platform wallet together with its single recorded key, saving immediately.
    /// Returns <see cref="HdWalletAddOutcome.DuplicateActive"/> when the unique
    /// <c>(MerchantId, Chain, Purpose)</c> index rejects it — a concurrent registration won — so the caller
    /// adopts the winner instead of registering a second. Keeps the EF-specific race translation inside
    /// Infrastructure (§4.4).
    /// </summary>
    Task<HdWalletAddOutcome> TryAddImportedPlatformKeyAsync(
        HdWallet wallet, DerivedKey derivedKey, CancellationToken cancellationToken = default);

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
