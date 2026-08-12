using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;

/// <summary>
/// Maps a chain to the ledger asset its native gas coin is booked under (TRX for TRON), for 5c platform gas
/// accounting. A chain absent from the map has no gas asset ⇒ its withdrawals book no gas cost (the fee still
/// rides the confirmation event, but the Ledger skips the journal). Kept out of the deposit asset catalog on
/// purpose, so adding a gas denomination never turns on native-coin deposit scanning.
/// </summary>
public sealed class GasAccountingOptions
{
    public IReadOnlyDictionary<Chain, Guid> GasAssetByChain { get; init; } = new Dictionary<Chain, Guid>();

    public Guid? Resolve(Chain chain) => GasAssetByChain.TryGetValue(chain, out var assetId) ? assetId : null;
}
