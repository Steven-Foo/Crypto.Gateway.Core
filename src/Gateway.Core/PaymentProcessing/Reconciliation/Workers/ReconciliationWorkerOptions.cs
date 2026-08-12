using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Workers;

public sealed class ReconciliationWorkerOptions
{
    /// <summary>Chains to reconcile. TRON-USDT at launch, so this is <c>[Chain.Tron]</c> today.</summary>
    public IReadOnlyList<Chain> Chains { get; init; } = [];

    /// <summary>
    /// How often to reconcile every active asset on each chain. Reconciliation is an audit backstop, not a
    /// hot path — a slower cadence than deposit/withdrawal scanning is appropriate (RPC per controlled address).
    /// </summary>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromMinutes(5);
}
