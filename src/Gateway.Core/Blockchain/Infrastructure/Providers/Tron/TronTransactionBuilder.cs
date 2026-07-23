using System.Text;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>
/// Builds an unsigned TRON TRC-20 <c>transfer</c> transaction via <c>/wallet/triggersmartcontract</c>
/// (§8). Read/compute only — it never signs, so holding a built transaction still cannot move funds
/// without the separate <see cref="CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts.ISigner"/>
/// (§10). Scope: TRC-20 tokens (e.g. USDT). Native TRX withdrawals use a different contract type
/// (<c>TransferContract</c> via <c>/wallet/createtransaction</c>) and are a documented follow-up,
/// symmetric to how native-TRX deposit detection was deferred.
///
/// <para><b>Money-out safety (deferred to L3 step 0):</b> the <c>txID</c> the node returns is NOT stable
/// across rebuilds — it stamps <c>ref_block</c>/<c>expiration</c>/<c>timestamp</c> at build time, so
/// building the same withdrawal twice yields a DIFFERENT <c>txID</c> the chain will not dedup. Before a
/// real broadcaster + signer are wired, the processing service must persist this built blob and
/// re-broadcast the SAME one on retry, never rebuild. (See withdrawal-module design note.)</para>
/// </summary>
public sealed class TronTransactionBuilder(
    ITronTxRpc rpc,
    IAssetCatalog assetCatalog,
    TronOptions options,
    ILogger<TronTransactionBuilder> logger) : ITransactionBuilder
{
    public async Task<UnsignedTransaction> BuildTransferAsync(
        BuildWithdrawalRequest request, CancellationToken cancellationToken = default)
    {
        var asset = await assetCatalog.FindByIdAsync(request.AssetId, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown asset {request.AssetId}.");

        if (asset.Chain != Chain.Tron || asset.IsNative || string.IsNullOrEmpty(asset.ContractAddress))
            throw new NotSupportedException(
                $"TRON transaction builder supports TRC-20 tokens only; asset {request.AssetId} ({asset.Symbol}) is not a TRON token contract.");

        var trigger = new TriggerSmartContractRequest
        {
            OwnerAddress = TronAddress.ToRawHex(request.FromAddress),
            ContractAddress = TronAddress.ToRawHex(asset.ContractAddress),
            Parameter = TronAbi.EncodeTransfer(request.ToAddress, request.Amount),
            FeeLimit = options.FeeLimitSun,
            CallValue = 0,
            Visible = false,
        };

        var result = await rpc.TriggerSmartContractAsync(trigger, cancellationToken);

        if (result.Result is not { Result: true } || result.Transaction is not { } transaction)
        {
            var code = result.Result?.Code ?? "UNKNOWN";
            var detail = TronErrorMessage.Decode(result.Result?.Message);
            logger.LogError(
                "triggersmartcontract rejected the transfer of asset {AssetId} to {To}: {Code} {Detail}",
                request.AssetId, request.ToAddress, code, detail);
            throw new InvalidOperationException($"triggersmartcontract rejected the transfer: {code} {detail}".TrimEnd());
        }

        // Carried opaquely to the signer, which adds the signature[] and returns the signed blob (§10).
        return new UnsignedTransaction(Encoding.UTF8.GetBytes(transaction.GetRawText()));
    }
}
