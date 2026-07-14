using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;

/// <summary>The chain's acknowledgement of an accepted broadcast: the transaction hash to track.</summary>
public sealed record BroadcastResult(string TransactionHash);

/// <summary>The on-chain status of a broadcast transaction, for confirmation tracking.</summary>
public sealed record TransactionStatus(long BlockNumber, bool Succeeded);

/// <summary>
/// Broadcasts an <em>already-signed</em> transaction and reads back its status (§8). It only ever sees a
/// signed blob — never a key. Broadcasting must be idempotent/safe to retry: re-broadcasting the same
/// signed transaction must not double-send (the chain dedups on the tx hash).
/// </summary>
public interface ITransactionBroadcaster
{
    Task<Result<BroadcastResult>> BroadcastAsync(Chain chain, byte[] signedPayload, CancellationToken cancellationToken = default);

    /// <summary>The transaction's status once mined, or null if not yet found on-chain.</summary>
    Task<TransactionStatus?> GetTransactionStatusAsync(Chain chain, string transactionHash, CancellationToken cancellationToken = default);
}
