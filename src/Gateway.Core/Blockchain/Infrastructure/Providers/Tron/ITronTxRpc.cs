using System.Text.Json;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>
/// The TRON node operations the money-out adapters need — building, broadcasting, and status — over the
/// native <c>/wallet/*</c> HTTP API (NOT the eth-compatible <c>jsonrpc</c> envelope the scanner's
/// <see cref="ITronRpc"/> uses). Segregated from <see cref="ITronRpc"/> so the scanner's read-only
/// guarantee stays intact (§8). <b>Keyless (§10):</b> <c>triggersmartcontract</c> returns an UNSIGNED
/// transaction, <c>broadcasttransaction</c> sends an ALREADY-SIGNED blob, and <c>gettransactioninfobyid</c>
/// only reads status — no private key ever crosses this interface. Implemented by <c>TronRpc</c>.
/// </summary>
public interface ITronTxRpc
{
    /// <summary><c>/wallet/triggersmartcontract</c> — builds an unsigned TRC-20 <c>transfer</c> transaction.</summary>
    Task<TronTriggerResultDto> TriggerSmartContractAsync(
        TriggerSmartContractRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>/wallet/broadcasttransaction</c> — submits an already-signed transaction object verbatim.
    /// Idempotent at the chain: re-broadcasting the same signed blob (same <c>txID</c>) does not
    /// double-send; the node reports it as a duplicate, which the broadcaster treats as success.
    /// </summary>
    Task<TronBroadcastResultDto> BroadcastTransactionAsync(
        JsonElement signedTransaction, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>/wallet/gettransactioninfobyid</c> — the mined transaction's info, or <c>null</c> when the node
    /// returns an empty object (not yet mined, or unknown/dropped).
    /// </summary>
    Task<TronTransactionInfoDto?> GetTransactionInfoAsync(
        string transactionId, CancellationToken cancellationToken = default);
}
