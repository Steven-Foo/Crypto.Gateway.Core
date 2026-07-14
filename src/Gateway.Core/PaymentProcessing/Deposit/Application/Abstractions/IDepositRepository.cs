using CryptoPaymentEngine.SharedKernel;
using DepositEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain.Deposit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application.Abstractions;

public enum DepositRecordOutcome
{
    /// <summary>The deposit was new and persisted.</summary>
    Recorded = 1,

    /// <summary>A deposit with the same <c>(Chain, TxHash, OutputIndex)</c> already existed — a re-scan. Skipped.</summary>
    Duplicate = 2,
}

public interface IDepositRepository
{
    /// <summary>
    /// Persists a newly-detected deposit, or reports <see cref="DepositRecordOutcome.Duplicate"/> if the
    /// dedup key already exists. The UNIQUE index is the arbiter (§7.3); the infra layer owns detecting
    /// the violation so the application never sees a provider exception.
    /// </summary>
    Task<DepositRecordOutcome> AddIfNewAsync(DepositEntity deposit, CancellationToken cancellationToken = default);

    /// <summary>Deposits still worth tracking for confirmation/reorg — those in Detected or Confirmed status.</summary>
    Task<IReadOnlyList<DepositEntity>> GetTrackableAsync(Chain chain, CancellationToken cancellationToken = default);

    /// <summary>Commits mutations made to tracked aggregates (and, via the outbox, any events they raised).</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
