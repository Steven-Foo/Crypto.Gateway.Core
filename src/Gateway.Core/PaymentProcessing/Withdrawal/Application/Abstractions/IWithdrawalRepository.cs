using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;

public enum WithdrawalRecordOutcome
{
    Recorded = 1,

    /// <summary>A withdrawal with the same <c>(MerchantId, MerchantTransactionId)</c> already existed. Skipped.</summary>
    Duplicate = 2,
}

public interface IWithdrawalRepository
{
    /// <summary>The idempotency arbiter: one withdrawal per client key per merchant, per kind (§7.3). Kind is
    /// part of the key so a user payout and a merchant cash-out may reuse the same reference without colliding.</summary>
    Task<WithdrawalEntity?> FindByMerchantTransactionIdAsync(
        Guid merchantId, WithdrawalKind kind, string merchantTransactionId, CancellationToken cancellationToken = default);

    Task<WithdrawalEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Withdrawals in any of the given statuses — the workers' working set.</summary>
    Task<IReadOnlyList<WithdrawalEntity>> GetByStatusesAsync(IReadOnlyCollection<WithdrawalStatus> statuses, CancellationToken cancellationToken = default);

    /// <summary>
    /// The set of hot-pool wallet ids currently leased — i.e. carrying a withdrawal in <c>Signing</c> or
    /// <c>Broadcast</c> (committed, not yet confirmed) on this chain. The allocator excludes these so each
    /// wallet processes one transaction at a time, held until it confirms.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> GetInFlightSourceWalletIdsAsync(Chain chain, CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent time each of <paramref name="walletIds"/> was used as a payout source (by the requesting
    /// withdrawal's <c>CreatedAt</c>), for least-recently-used pool selection. A wallet with no history is
    /// absent from the map (treated as never used ⇒ most eligible).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetWalletLastUsedAsync(
        IReadOnlyCollection<Guid> walletIds, CancellationToken cancellationToken = default);

    Task<WithdrawalRecordOutcome> AddIfNewAsync(WithdrawalEntity withdrawal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a just-signed withdrawal (→ <c>Signing</c> with its <c>SourceWalletId</c>). Returns
    /// <c>false</c> — detaching the entity so the context stays usable — when the save loses a concurrency
    /// race: another withdrawal already leased the wallet (the <c>UX_Withdrawal_InFlight_SourceWallet</c> unique
    /// index) or another worker already advanced this withdrawal (rowversion). Both mean "leave it, re-allocate
    /// next pass"; nothing was broadcast, so there is no double-send. Keeps the EF-specific race translation
    /// inside Infrastructure (§4.4).
    /// </summary>
    Task<bool> TrySaveSignedAsync(WithdrawalEntity withdrawal, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
