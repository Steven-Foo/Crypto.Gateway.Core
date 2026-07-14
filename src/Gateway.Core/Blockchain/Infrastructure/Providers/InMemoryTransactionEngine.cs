using System.Security.Cryptography;
using System.Text;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;

/// <summary>
/// Deterministic, node-free build/broadcast for Development and tests — the DI seam a real per-chain
/// transaction builder/broadcaster plugs into. Its driving knobs (<see cref="MinedAtBlock"/>,
/// <see cref="NextBroadcastSucceeds"/>) let a test script confirmation and broadcast-failure paths.
/// Thread-safe.
/// </summary>
public sealed class InMemoryTransactionEngine : ITransactionBuilder, ITransactionBroadcaster
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _broadcast = [];

    /// <summary>The block a broadcast transaction is "mined" into (for confirmation-count testing).</summary>
    public long MinedAtBlock { get; set; } = 1000;

    /// <summary>Toggle to simulate a rejected broadcast.</summary>
    public bool NextBroadcastSucceeds { get; set; } = true;

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
            _broadcast[hash] = MinedAtBlock;

        return Task.FromResult(Result.Success(new BroadcastResult(hash)));
    }

    public Task<TransactionStatus?> GetTransactionStatusAsync(Chain chain, string transactionHash, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult(_broadcast.TryGetValue(transactionHash, out var block)
                ? new TransactionStatus(block, Succeeded: true)
                : null);
    }
}
