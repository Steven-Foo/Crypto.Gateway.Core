using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>
/// Broadcasts an already-signed TRON transaction via <c>/wallet/broadcasttransaction</c> and reads its
/// status via <c>/wallet/gettransactioninfobyid</c> (§8). It only ever sees a signed blob — never a key
/// (§10). Idempotent/safe to retry: the transaction's <c>txID</c> is fixed at build/sign time, so
/// re-broadcasting the same blob yields the same <c>txID</c>; the node's duplicate response is treated
/// as success (the network already has it).
/// </summary>
public sealed class TronTransactionBroadcaster(
    ITronTxRpc rpc,
    ILogger<TronTransactionBroadcaster> logger) : ITransactionBroadcaster
{
    // The node reports an already-known transaction (our idempotent re-broadcast) with this code — it is
    // success, not failure: the network already accepted this exact signed blob.
    private const string DuplicateTransactionCode = "DUP_TRANSACTION_ERROR";

    public async Task<Result<BroadcastResult>> BroadcastAsync(
        Chain chain, byte[] signedPayload, CancellationToken cancellationToken = default)
    {
        JsonElement signed;
        string? txId;
        try
        {
            using var document = JsonDocument.Parse(signedPayload);
            signed = document.RootElement.Clone();
            txId = signed.TryGetProperty("txID", out var element) ? element.GetString() : null;
        }
        catch (JsonException)
        {
            return Result.Failure<BroadcastResult>(Error.Failure("broadcast.malformed", "Signed transaction is not valid JSON."));
        }

        if (string.IsNullOrEmpty(txId))
            return Result.Failure<BroadcastResult>(Error.Failure("broadcast.malformed", "Signed transaction is missing its txID."));

        var result = await rpc.BroadcastTransactionAsync(signed, cancellationToken);

        if (result.Result)
            return Result.Success(new BroadcastResult(txId));

        if (string.Equals(result.Code, DuplicateTransactionCode, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Broadcast of {TxId} returned {Code} — already accepted by the network; treating as success.", txId, result.Code);
            return Result.Success(new BroadcastResult(txId));
        }

        var reason = TronErrorMessage.Decode(result.Message) ?? "The node rejected the transaction.";
        logger.LogError("Broadcast of {TxId} failed: {Code} {Reason}", txId, result.Code, reason);
        return Result.Failure<BroadcastResult>(Error.Failure($"broadcast.{result.Code ?? "failed"}", reason));
    }

    public async Task<TransactionStatus?> GetTransactionStatusAsync(
        Chain chain, string transactionHash, CancellationToken cancellationToken = default)
    {
        var info = await rpc.GetTransactionInfoAsync(transactionHash, cancellationToken);

        // Null / no block yet ⇒ not mined (or unknown/dropped) — the confirmation tracker keeps polling.
        if (info?.BlockNumber is not { } block || block <= 0)
            return null;

        // The node's failure signal is the TOP-LEVEL `result` field ("FAILED" only on genuine failure, absent
        // on success — native and smart-contract alike). `receipt.result` is a SEPARATE, VM-only field: the
        // node only ever populates it for a smart-contract call (e.g. a TRC-20 transfer), never for a native
        // system contract (a plain TRX transfer, FreezeBalanceV2, DelegateResource, ...). So a successful
        // native transaction has BOTH fields absent — treating an absent receipt.result as failure (the old
        // bug here) silently marked every successful native tx as reverted and left it stuck forever.
        var topLevelFailed = string.Equals(info.Result, TronConstants.TransactionResultFailed, StringComparison.Ordinal);
        var receiptResult = info.Receipt?.Result;
        var receiptFailed = !string.IsNullOrEmpty(receiptResult)
            && !string.Equals(receiptResult, TronConstants.ContractRetSuccess, StringComparison.Ordinal);

        if (!topLevelFailed && receiptResult is null && info.Result is null)
        {
            // Both signals absent: the common native-transaction success shape. Log once, defensively — if
            // TRON's real failure shape ever differs from what's documented here, this makes it visible
            // instead of silently mis-classifying again.
            logger.LogDebug(
                "Tron tx {TxHash} mined at block {Block} with no top-level result and no receipt.result — " +
                "treating as a successful native transaction.", transactionHash, block);
        }

        var succeeded = !topLevelFailed && !receiptFailed;
        var energyUsed = info.Receipt?.EnergyUsageTotal is { } total ? new System.Numerics.BigInteger(total) : System.Numerics.BigInteger.Zero;
        return new TransactionStatus(block, succeeded, new System.Numerics.BigInteger(info.Fee), energyUsed); // fee in sun, energy in units — never the same thing (§14)
    }
}
