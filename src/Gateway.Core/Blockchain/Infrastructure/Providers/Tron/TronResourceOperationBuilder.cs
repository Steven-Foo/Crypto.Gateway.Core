using System.Text;
using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>
/// Builds unsigned TRON resource transactions (§8): <c>FreezeBalanceV2</c> to stake TRX for energy, and
/// <c>DelegateResource</c> to lend that energy to a deposit address before a sweep. Read/compute only — it
/// never signs, so a built blob still cannot lock or move funds without the separate <c>ISigner</c> (§10).
/// The unsigned blob (txID / raw_data / raw_data_hex) flows through the SAME <c>ISigner</c> and
/// <c>ITransactionBroadcaster</c> as a transfer. Staking/delegation lock <em>recoverable</em> TRX — no expense
/// (the ledger impact deferral, §15.4).
/// </summary>
public sealed class TronResourceOperationBuilder(
    ITronResourceRpc rpc, ILogger<TronResourceOperationBuilder> logger) : IResourceOperationBuilder
{
    public async Task<UnsignedTransaction> BuildStakeForEnergyAsync(
        StakeForEnergyRequest request, CancellationToken cancellationToken = default)
    {
        var response = await rpc.FreezeBalanceV2Async(new FreezeBalanceV2Request
        {
            OwnerAddress = TronAddress.ToRawHex(request.OwnerAddress),
            FrozenBalance = ToSun(request.TrxAmountSun, nameof(request.TrxAmountSun)),
            Resource = "ENERGY",
            Visible = false,
        }, cancellationToken);

        return Unsigned("freezebalancev2", response);
    }

    public async Task<UnsignedTransaction> BuildDelegateEnergyAsync(
        DelegateEnergyRequest request, CancellationToken cancellationToken = default)
    {
        var response = await rpc.DelegateResourceAsync(new DelegateResourceRequest
        {
            OwnerAddress = TronAddress.ToRawHex(request.OwnerAddress),
            ReceiverAddress = TronAddress.ToRawHex(request.ReceiverAddress),
            Balance = ToSun(request.TrxAmountSun, nameof(request.TrxAmountSun)),
            Resource = "ENERGY",
            Lock = false, // reclaimable immediately (undelegate is a future refinement)
            Visible = false,
        }, cancellationToken);

        return Unsigned("delegateresource", response);
    }

    /// <summary>The node returns the unsigned tx at the top level, or <c>{ "Error": "&lt;hex-ascii&gt;" }</c>.</summary>
    private UnsignedTransaction Unsigned(string operation, JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("Error", out var error))
        {
            var detail = TronErrorMessage.Decode(error.GetString());
            logger.LogError("{Operation} was rejected by the node: {Detail}", operation, detail);
            throw new InvalidOperationException($"{operation} was rejected: {detail}");
        }

        // A well-formed unsigned transaction carries a txID the signer hashes/checks.
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("txID", out _))
            throw new InvalidOperationException($"{operation} returned no transaction and no error.");

        return new UnsignedTransaction(Encoding.UTF8.GetBytes(response.GetRawText()));
    }

    private static long ToSun(System.Numerics.BigInteger amount, string name)
    {
        if (amount <= 0 || amount > long.MaxValue)
            throw new ArgumentOutOfRangeException(name, amount, "TRX amount (sun) must be positive and within TRON's range.");
        return (long)amount;
    }
}
