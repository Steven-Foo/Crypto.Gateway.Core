using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;

/// <summary>
/// Builds an unsigned transfer of the chain's <b>native coin</b> (TRX on TRON) — a plain value transfer, not a
/// token contract call (§8). Deliberately assetless: the native coin has no <c>AssetId</c>, so it never enters
/// the deposit catalog (which would switch on native-coin deposit scanning). Read/compute only — it never signs,
/// so a built blob still cannot move funds without the separate <c>ISigner</c> (§10). The unsigned blob flows
/// through the same <c>ISigner</c>/<c>ITransactionBroadcaster</c> as a token transfer. Used to top up a deposit
/// address's bandwidth-TRX and to sweep residual TRX to the energy (gas hub) wallet.
/// </summary>
public interface INativeTransferBuilder
{
    Task<UnsignedTransaction> BuildNativeTransferAsync(
        Chain chain, string fromAddress, string toAddress, BigInteger amountSun, CancellationToken cancellationToken = default);
}
