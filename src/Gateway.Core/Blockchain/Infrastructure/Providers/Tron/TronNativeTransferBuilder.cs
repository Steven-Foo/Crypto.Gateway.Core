using System.Numerics;
using System.Text;
using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>
/// Builds an unsigned native-TRX transfer via <c>/wallet/createtransaction</c> (§8) — a plain
/// <c>TransferContract</c>, not a token call. Read/compute only; it never signs (§10), and the unsigned blob
/// (txID / raw_data / raw_data_hex) flows through the SAME <c>ISigner</c>/<c>ITransactionBroadcaster</c> as a
/// TRC-20 transfer. Used to top up a deposit address's bandwidth-TRX and to sweep residual TRX to the gas hub.
/// </summary>
public sealed class TronNativeTransferBuilder(
    ITronTxRpc rpc, ILogger<TronNativeTransferBuilder> logger) : INativeTransferBuilder
{
    public async Task<UnsignedTransaction> BuildNativeTransferAsync(
        Chain chain, string fromAddress, string toAddress, BigInteger amountSun, CancellationToken cancellationToken = default)
    {
        if (chain != Chain.Tron)
            throw new NotSupportedException($"{nameof(TronNativeTransferBuilder)} builds TRON native transfers only, not {chain}.");

        if (amountSun <= 0 || amountSun > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(amountSun), amountSun, "TRX amount (sun) must be positive and within TRON's range.");

        var response = await rpc.CreateTransactionAsync(new CreateTransactionRequest
        {
            OwnerAddress = TronAddress.ToRawHex(fromAddress),
            ToAddress = TronAddress.ToRawHex(toAddress),
            Amount = (long)amountSun,
            Visible = false,
        }, cancellationToken);

        // The node returns the unsigned tx at the top level, or { "Error": "<hex-ascii>" }.
        if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("Error", out var error))
        {
            var detail = TronErrorMessage.Decode(error.GetString());
            logger.LogError("createtransaction was rejected by the node: {Detail}", detail);
            throw new InvalidOperationException($"createtransaction was rejected: {detail}");
        }

        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("txID", out _))
            throw new InvalidOperationException("createtransaction returned no transaction and no error.");

        return new UnsignedTransaction(Encoding.UTF8.GetBytes(response.GetRawText()));
    }
}
