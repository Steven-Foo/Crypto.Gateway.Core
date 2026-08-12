using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;

/// <summary>The chain's acknowledgement of an accepted broadcast: the transaction hash to track.</summary>
public sealed record BroadcastResult(string TransactionHash);

/// <summary>
/// The on-chain status of a broadcast transaction, for confirmation tracking. <see cref="FeeSun"/> is the
/// native-coin fee the sender actually paid (TRX in sun for TRON), read from the receipt — used by 5c platform
/// gas accounting; defaults to zero (the in-memory engine charges no fee, so dev books no gas cost, §14).
/// </summary>
public sealed record TransactionStatus(long BlockNumber, bool Succeeded, BigInteger FeeSun = default);

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
