using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;

/// <summary>A request to build an on-chain transfer of <paramref name="Amount"/> base units of an asset.</summary>
public sealed record BuildWithdrawalRequest(Chain Chain, Guid AssetId, string FromAddress, string ToAddress, BigInteger Amount);

/// <summary>An unsigned transaction blob, ready for the signer. Opaque bytes to everyone but the chain adapter.</summary>
public sealed record UnsignedTransaction(byte[] Payload);

/// <summary>
/// Builds an unsigned transfer transaction for a chain (§8). Read/compute only — it never signs, so a
/// module that builds transactions still cannot move funds without the separate <c>ISigner</c> (§10).
/// </summary>
public interface ITransactionBuilder
{
    Task<UnsignedTransaction> BuildTransferAsync(BuildWithdrawalRequest request, CancellationToken cancellationToken = default);
}
