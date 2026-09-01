using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>
/// Verifies a TRON transaction an operator recorded, against the chain itself.
///
/// <para>Read-only and keyless (§10). It answers one question — "did this hash really move at least this much
/// of this asset to this address, and is it irreversible?" — because everything downstream of it discharges a
/// merchant's reserved balance or increases recorded custody. An unverified claim there would be a silent
/// money error, not a visible one.</para>
///
/// <para>"Confirmed" means <b>solidified</b>, not merely mined. TRON's solidified block is its irreversibility
/// point, so a transaction at or below it cannot be reorganised away. Accepting a merely-mined transaction
/// would let a dropped or reorged transfer permanently settle a merchant obligation against nothing.</para>
///
/// <para>The destination and amount come from the same <see cref="TronChainAdapter.TryMapTransfer"/> the
/// deposit scanner uses — deliberately, so verification and detection can never disagree about what a TRC-20
/// Transfer log means. That mapping is the money-critical step and is unit-tested against a real USDT vector.</para>
/// </summary>
public sealed class TronTransactionVerifier(
    ITronRpc rpc,
    IAssetCatalog assetCatalog,
    ILogger<TronTransactionVerifier> logger) : ITransactionVerifier
{
    public async Task<Result<VerifiedTransfer>> VerifyAsync(
        VerifyTransferRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Chain != Chain.Tron)
            return Result.Failure<VerifiedTransfer>(TransactionVerificationErrors.Unsupported);

        var hash = Normalize(request.TransactionHash);
        if (string.IsNullOrWhiteSpace(hash))
            return Result.Failure<VerifiedTransfer>(TransactionVerificationErrors.NotFound);

        // An empty {} response means "not mined, unknown, or dropped" — all indistinguishable at the node, and
        // all mean the same thing to an operator: this hash is not (yet) a settled fact.
        var info = await rpc.GetTransactionInfoAsync(hash, cancellationToken);
        if (info?.BlockNumber is not { } blockNumber)
            return Result.Failure<VerifiedTransfer>(TransactionVerificationErrors.NotFound);

        // Mined but reverted. A smart-contract call reports failure on the receipt; a native transfer reports
        // it at the top level and leaves the receipt empty — so both must be checked (see TronTransactionInfoDto).
        if (string.Equals(info.Result, "FAILED", StringComparison.OrdinalIgnoreCase) ||
            (info.Receipt?.Result is { Length: > 0 } r && !string.Equals(r, "SUCCESS", StringComparison.OrdinalIgnoreCase)))
            return Result.Failure<VerifiedTransfer>(TransactionVerificationErrors.Failed);

        var solidified = await rpc.GetSolidifiedBlockNumberAsync(cancellationToken);
        if (blockNumber > solidified)
            return Result.Failure<VerifiedTransfer>(TransactionVerificationErrors.NotConfirmed);

        var asset = await assetCatalog.FindByIdAsync(request.AssetId, cancellationToken);
        if (asset is null || string.IsNullOrWhiteSpace(asset.ContractAddress))
            return Result.Failure<VerifiedTransfer>(TransactionVerificationErrors.AssetMismatch);

        // Re-read the transfer logs of just this block and pick out this transaction's. One block, one
        // contract — a cheap, bounded read, and it reuses the scanner's mapper rather than re-deriving how a
        // Transfer log decodes.
        var contractMap = await BuildContractMapAsync(request.AssetId, cancellationToken);
        if (contractMap.Count == 0)
            return Result.Failure<VerifiedTransfer>(TransactionVerificationErrors.AssetMismatch);

        var logs = await rpc.GetTransferLogsAsync(blockNumber, blockNumber, contractMap.Keys.ToList(), cancellationToken);

        var transfers = logs
            .Where(l => string.Equals(Normalize(l.TransactionHash), hash, StringComparison.OrdinalIgnoreCase))
            .Select(l => TronChainAdapter.TryMapTransfer(l, contractMap, out var t) ? t : null)
            .Where(t => t is not null)
            .Select(t => t!)
            .ToList();

        if (transfers.Count == 0)
        {
            logger.LogWarning(
                "Verification: {Hash} is confirmed in block {Block} but carries no transfer of asset {AssetId}.",
                hash, blockNumber, request.AssetId);
            return Result.Failure<VerifiedTransfer>(TransactionVerificationErrors.AssetMismatch);
        }

        // A single transaction may pay several addresses. Match on the destination first, so a batch payment
        // that includes the merchant is accepted for the merchant's own leg rather than rejected wholesale.
        var toExpected = transfers
            .Where(t => string.Equals(t.Address, request.ExpectedTo.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (toExpected.Count == 0)
            return Result.Failure<VerifiedTransfer>(TransactionVerificationErrors.DestinationMismatch);

        // Several legs to the same address in one transaction are summed: what matters is how much arrived.
        var received = toExpected.Aggregate(System.Numerics.BigInteger.Zero, (sum, t) => sum + t.Amount);
        if (received < request.MinimumAmount)
            return Result.Failure<VerifiedTransfer>(TransactionVerificationErrors.AmountMismatch);

        return Result.Success(new VerifiedTransfer(hash, toExpected[0].Address, received, blockNumber));
    }

    /// <summary>
    /// The single expected asset's contract, keyed exactly as <see cref="TronChainAdapter.TryMapTransfer"/>
    /// expects — it normalises the log's address before lookup, so the key must be whatever
    /// <c>TronAddress.ToEvmHex</c> produces, unaltered.
    /// </summary>
    private async Task<Dictionary<string, Guid>> BuildContractMapAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var active = await assetCatalog.GetActiveAsync(cancellationToken);
        var map = new Dictionary<string, Guid>();

        foreach (var asset in active.Where(a =>
                     a.AssetId == assetId && a.Chain == Chain.Tron && !a.IsNative && a.ContractAddress is not null))
        {
            try
            {
                map[Addresses.TronAddress.ToEvmHex(asset.ContractAddress!)] = asset.AssetId;
            }
            catch (FormatException ex)
            {
                logger.LogWarning(ex, "Verification: asset {AssetId} has an unusable contract address.", asset.AssetId);
            }
        }

        return map;
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();
}
