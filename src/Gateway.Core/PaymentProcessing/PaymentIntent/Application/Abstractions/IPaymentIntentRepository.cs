using CryptoPaymentEngine.SharedKernel;
using PaymentIntentEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain.PaymentIntent;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Abstractions;

/// <summary>Outcome of inserting a new intent — the DB unique indexes, not app logic, decide (§7.3).</summary>
public enum PaymentIntentAddOutcome
{
    Added,
    /// <summary>An intent already exists for this <c>(merchant, reference)</c> — a duplicate request.</summary>
    DuplicateReference,
    /// <summary>Another live intent grabbed this address first — the caller should retry with a fresh one.</summary>
    AddressBusy,
}

/// <summary>A reusable deposit address: one a merchant already owns that no live invoice is holding.</summary>
public sealed record ReusableAddress(Guid WalletId, string Address);

public interface IPaymentIntentRepository
{
    /// <summary>Idempotency lookup: the intent already created for this merchant reference, if any.</summary>
    Task<PaymentIntentEntity?> FindByMerchantReferenceAsync(Guid merchantId, string merchantTransactionId, CancellationToken cancellationToken = default);

    /// <summary>A previously-used address for this merchant+chain that no Waiting intent holds — the pool's free list.</summary>
    Task<ReusableAddress?> FindReusableAddressAsync(Guid merchantId, Chain chain, CancellationToken cancellationToken = default);

    /// <summary>The single live (Waiting) invoice holding an address — for matching a confirmed deposit. Returned tracked.</summary>
    Task<PaymentIntentEntity?> FindWaitingByWalletAsync(Guid walletId, CancellationToken cancellationToken = default);

    /// <summary>Whether a deposit has already resolved some intent — makes matching idempotent per deposit across redelivery.</summary>
    Task<bool> IsDepositMatchedAsync(Guid depositId, CancellationToken cancellationToken = default);

    /// <summary>Inserts a new intent, letting the DB's unique indexes arbitrate idempotency and the address reservation.</summary>
    Task<PaymentIntentAddOutcome> TryAddAsync(PaymentIntentEntity intent, CancellationToken cancellationToken = default);

    /// <summary>Flips lapsed Waiting invoices to Expired, freeing their addresses. Returns how many were swept.</summary>
    Task<int> ExpireStaleAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
