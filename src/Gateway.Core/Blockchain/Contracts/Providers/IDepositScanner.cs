using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;

/// <summary>
/// One incoming on-chain transfer the scanner observed. <see cref="Amount"/> is exact unsigned base
/// units. <see cref="OutputIndex"/> (log index / vout) disambiguates several transfers in one tx, so
/// <c>(Chain, TransactionHash, OutputIndex)</c> is a stable natural key for deduplication.
/// The scanner resolves <see cref="AssetId"/> from the chain's asset catalog; consumers treat it as opaque.
/// </summary>
public sealed record DetectedTransfer(
    Chain Chain,
    string Address,
    Guid AssetId,
    BigInteger Amount,
    string TransactionHash,
    int OutputIndex,
    long BlockNumber,
    string BlockHash);

/// <summary>
/// A read-only chain capability (§8): find incoming transfers in a block range. It is deliberately
/// tiny and read-only — a module that scans deposits gets no ability to sign or broadcast (§10).
/// Implementations are chain-specific adapters (in-memory for dev/test; JSON-RPC for staging/prod),
/// selected purely by DI configuration.
/// </summary>
public interface IDepositScanner
{
    /// <summary>
    /// Returns candidate incoming transfers in <c>[fromBlock, toBlock]</c>. The caller decides which
    /// are ours (via the wallet directory) and which clear the deposit policy — the scanner makes no
    /// ownership or business decision.
    /// </summary>
    Task<IReadOnlyList<DetectedTransfer>> ScanAsync(
        Chain chain, long fromBlock, long toBlock, CancellationToken cancellationToken = default);
}
