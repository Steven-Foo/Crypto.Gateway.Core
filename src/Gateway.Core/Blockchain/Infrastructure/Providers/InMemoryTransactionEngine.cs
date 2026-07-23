using System.Security.Cryptography;
using System.Text;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;

/// <summary>
/// Deterministic, node-free build/broadcast for Development and tests — the DI seam a real per-chain
/// transaction builder/broadcaster plugs into (§8). It stands in for the chain only; the withdrawal state
/// machine, ledger, and workers around it are the real ones.
///
/// <para>Its driving knobs let a test script every money-out chain scenario without a node — the "Level 1
/// (fake blockchain)" harness:
/// <list type="bullet">
///   <item><see cref="NextBroadcastSucceeds"/> — the node rejects the broadcast (transient send failure);</item>
///   <item><see cref="NextTransactionReverts"/> — the tx is accepted and mined but <em>reverts</em> on-chain
///   (Succeeded=false), so custody may not have moved as intended;</item>
///   <item><see cref="MineDelayPolls"/> — the tx is not visible for the first N status polls, then appears
///   (inclusion / network delay);</item>
///   <item><see cref="OrphanTransaction"/> — a mined tx disappears from the canonical chain (reorg after
///   broadcast), so its status goes back to "not found".</item>
/// </list>
/// A tx's fate (mined block, revert, delay) is captured when it is broadcast, so re-broadcasting the same
/// signed blob is idempotent and cannot double-send or change the outcome — the chain dedups on the tx hash.</para>
///
/// Thread-safe.
/// </summary>
public sealed class InMemoryTransactionEngine : ITransactionBuilder, ITransactionBroadcaster
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TxRecord> _broadcast = [];

    /// <summary>The block a broadcast transaction is "mined" into (for confirmation-count testing).</summary>
    public long MinedAtBlock { get; set; } = 1000;

    /// <summary>Toggle to simulate a rejected broadcast (the node refuses to accept the transaction).</summary>
    public bool NextBroadcastSucceeds { get; set; } = true;

    /// <summary>
    /// When true, a transaction broadcast from now on is accepted and mined but reports <c>Succeeded=false</c>
    /// from its status — an on-chain revert. The withdrawal must then be left in Broadcast for ops, never
    /// settled (funds may not have moved as intended). Captured per-tx at broadcast time.
    /// </summary>
    public bool NextTransactionReverts { get; set; }

    /// <summary>
    /// The number of status polls that return null ("not yet mined") before a transaction becomes visible,
    /// simulating inclusion / network delay. Captured per-tx at broadcast time; each poll decrements it.
    /// </summary>
    public int MineDelayPolls { get; set; }

    private sealed class TxRecord
    {
        public required long Block;
        public required bool Succeeded;
        public int RemainingDelayPolls;
    }

    public Task<UnsignedTransaction> BuildTransferAsync(BuildWithdrawalRequest request, CancellationToken cancellationToken = default)
    {
        var payload = Encoding.UTF8.GetBytes(
            $"unsigned:{request.Chain}:{request.AssetId}:{request.FromAddress}->{request.ToAddress}:{request.Amount}");
        return Task.FromResult(new UnsignedTransaction(payload));
    }

    public Task<Result<BroadcastResult>> BroadcastAsync(Chain chain, byte[] signedPayload, CancellationToken cancellationToken = default)
    {
        if (!NextBroadcastSucceeds)
            return Task.FromResult(Result.Failure<BroadcastResult>(Error.Failure("broadcast.failed", "The node rejected the transaction.")));

        // Deterministic hash of the signed blob — re-broadcasting the same signed tx yields the same hash,
        // so it can't double-send (mirrors real chains deduping on tx hash).
        var hash = "0x" + Convert.ToHexString(SHA256.HashData(signedPayload)).ToLowerInvariant();
        lock (_gate)
        {
            // Idempotent: the tx's fate is fixed on first broadcast; a retry of the same blob keeps it.
            if (!_broadcast.ContainsKey(hash))
                _broadcast[hash] = new TxRecord
                {
                    Block = MinedAtBlock,
                    Succeeded = !NextTransactionReverts,
                    RemainingDelayPolls = MineDelayPolls,
                };
        }

        return Task.FromResult(Result.Success(new BroadcastResult(hash)));
    }

    public Task<TransactionStatus?> GetTransactionStatusAsync(Chain chain, string transactionHash, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_broadcast.TryGetValue(transactionHash, out var record))
                return Task.FromResult<TransactionStatus?>(null); // never broadcast, or orphaned by a reorg

            if (record.RemainingDelayPolls > 0)
            {
                record.RemainingDelayPolls--;
                return Task.FromResult<TransactionStatus?>(null); // accepted but not yet mined (delay)
            }

            return Task.FromResult<TransactionStatus?>(new TransactionStatus(record.Block, record.Succeeded));
        }
    }

    /// <summary>
    /// Simulates a reorg <em>after</em> broadcast: the previously-mined transaction is dropped from the
    /// canonical chain, so subsequent status polls return null again. The confirmation tracker must not have
    /// settled it in the meantime, and must not settle it now.
    /// </summary>
    public void OrphanTransaction(string transactionHash)
    {
        lock (_gate)
            _broadcast.Remove(transactionHash);
    }
}
