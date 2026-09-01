using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;

public enum TopUpRecordOutcome
{
    Recorded = 1,

    /// <summary>A top-up with the same transaction hash already exists — the same on-chain transfer was
    /// recorded twice. Skipped, never double-counted.</summary>
    Duplicate = 2,
}

public interface IHotWalletTopUpRepository
{
    /// <summary>
    /// Persists a top-up, returning <see cref="TopUpRecordOutcome.Duplicate"/> when the unique transaction
    /// hash index rejects it. The DB is the arbiter (§7.3): two operators recording the same transfer
    /// concurrently would both pass a check-then-act read, and custody would be credited twice for one
    /// transfer.
    /// </summary>
    Task<TopUpRecordOutcome> AddIfNewAsync(HotWalletTopUp topUp, CancellationToken cancellationToken = default);

    Task<HotWalletTopUp?> FindByTransactionHashAsync(string transactionHash, CancellationToken cancellationToken = default);

    /// <summary>Recorded top-ups, newest first — the ops activity screen. Optionally narrowed to one chain.</summary>
    Task<(IReadOnlyList<HotWalletTopUp> Items, int TotalCount)> SearchAsync(
        Chain? chain, int page, int pageSize, CancellationToken cancellationToken = default);
}
