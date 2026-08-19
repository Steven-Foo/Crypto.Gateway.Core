using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;

/// <summary>
/// Resolves a merchant's whitelisted settlement (cash-out) address for a chain — the fixed destination of a
/// Merchant Withdrawal. The cash-out flow reads the destination through this and nothing else (§4.5), so a
/// compromised merchant API key can never redirect earnings: the destination is never client-supplied.
/// </summary>
public interface IMerchantSettlementDirectory
{
    /// <summary>The registered settlement address for <paramref name="chain"/>, or null if none is registered.</summary>
    Task<string?> FindSettlementAddressAsync(Guid merchantId, Chain chain, CancellationToken cancellationToken = default);
}
