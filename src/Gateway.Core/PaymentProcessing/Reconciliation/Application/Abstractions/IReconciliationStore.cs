using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Application.Abstractions;

/// <summary>
/// The latest reconciliation snapshot per (chain, asset), in MongoDB — a derived read model, never money
/// truth (§2). Upsert-by-(chain, asset): one current document, overwritten each pass.
/// </summary>
public interface IReconciliationStore
{
    Task UpsertAsync(ReconciliationSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<ReconciliationSnapshot?> GetAsync(Chain chain, Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>Every current snapshot (one per chain+asset) — the read behind the ops custody-status view.</summary>
    Task<IReadOnlyList<ReconciliationSnapshot>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>Append-only time series of reconciliation observations (MongoDB) — the audit trail of custody
/// drift over time. Never money truth (§2).</summary>
public interface IReconciliationHistoryStore
{
    Task AppendAsync(ReconciliationSnapshot snapshot, CancellationToken cancellationToken = default);
}
