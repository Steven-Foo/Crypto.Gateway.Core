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

    /// <summary>
    /// Every whitelisted settlement wallet on the platform, for the periodic re-screen.
    ///
    /// <para>Unpaged deliberately. This set is one row per merchant per chain — hundreds, not millions —
    /// and the caller screens every one of them anyway, so paging would add a cursor to maintain and a
    /// class of bug (a wallet skipped because it moved between pages) in exchange for nothing.</para>
    /// </summary>
    Task<IReadOnlyList<SettlementWalletRef>> ListAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>One whitelisted cash-out destination, identified by the merchant that owns it.</summary>
public sealed record SettlementWalletRef(Guid MerchantId, Chain Chain, string Address);
