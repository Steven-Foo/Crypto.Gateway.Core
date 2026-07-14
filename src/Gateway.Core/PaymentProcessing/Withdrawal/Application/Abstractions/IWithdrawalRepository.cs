using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;

public enum WithdrawalRecordOutcome
{
    Recorded = 1,

    /// <summary>A withdrawal with the same <c>(MerchantId, IdempotencyKey)</c> already existed. Skipped.</summary>
    Duplicate = 2,
}

public interface IWithdrawalRepository
{
    /// <summary>The idempotency arbiter: one withdrawal per client key per merchant (§7.3).</summary>
    Task<WithdrawalEntity?> FindByIdempotencyKeyAsync(Guid merchantId, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<WithdrawalEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Withdrawals in any of the given statuses — the workers' working set.</summary>
    Task<IReadOnlyList<WithdrawalEntity>> GetByStatusesAsync(IReadOnlyCollection<WithdrawalStatus> statuses, CancellationToken cancellationToken = default);

    Task<WithdrawalRecordOutcome> AddIfNewAsync(WithdrawalEntity withdrawal, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
