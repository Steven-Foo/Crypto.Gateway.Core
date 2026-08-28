using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>
/// Estimates a TRC-20 transfer's real energy cost via <c>/wallet/triggerconstantcontract</c> — the exact
/// same ABI-encoded call <see cref="TronTransactionBuilder"/> would actually broadcast, but simulated: no
/// signature, no fee, no broadcast, no funds moved. Mirrors the builder's asset lookup/ABI encoding so the
/// estimate is genuinely of the same call a real send would make, not an approximation of it.
/// </summary>
public sealed class TronEnergyEstimator(ITronTxRpc rpc, IAssetCatalog assetCatalog) : IEnergyEstimator
{
    public async Task<EnergyEstimate> EstimateTransferEnergyAsync(
        EstimateTransferEnergyRequest request, CancellationToken cancellationToken = default)
    {
        var asset = await assetCatalog.FindByIdAsync(request.AssetId, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown asset {request.AssetId}.");

        if (asset.Chain != Chain.Tron || asset.IsNative || string.IsNullOrEmpty(asset.ContractAddress))
        {
            throw new NotSupportedException(
                $"TRON energy estimator supports TRC-20 tokens only; asset {request.AssetId} ({asset.Symbol}) is not a TRON token contract.");
        }

        var trigger = new TriggerConstantContractRequest
        {
            OwnerAddress = TronAddress.ToRawHex(request.FromAddress),
            ContractAddress = TronAddress.ToRawHex(asset.ContractAddress),
            Parameter = TronAbi.EncodeTransfer(request.ToAddress, request.Amount),
            Visible = false,
        };

        var result = await rpc.TriggerConstantContractAsync(trigger, cancellationToken);

        // result.result=true only means the node ran the simulation; a populated message (e.g. "REVERT
        // opcode executed") is the actual failure signal, same convention observed live against Nile.
        var message = TronErrorMessage.Decode(result.Result?.Message);
        var succeeded = string.IsNullOrEmpty(message);

        return new EnergyEstimate(succeeded, new BigInteger(result.EnergyUsed), succeeded ? null : message);
    }
}
