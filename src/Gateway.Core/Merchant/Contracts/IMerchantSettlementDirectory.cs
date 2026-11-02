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
    /// A specific settlement wallet the merchant chose for one cash-out — returned only if it belongs to
    /// <paramref name="merchantId"/>, is on <paramref name="chain"/>, and is <b>approved</b> (the active one or an
    /// approved standby). Pending, rejected and cancelled wallets, another merchant's wallet, or a wallet on
    /// another chain all return null. The choice is still limited to addresses platform staff approved (§10);
    /// only WHICH approved address is the caller's.
    /// </summary>
    Task<ApprovedSettlementWallet?> FindApprovedSettlementWalletAsync(
        Guid merchantId, Chain chain, Guid walletId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every approved settlement wallet on the platform (active AND standby — a standby can be chosen for a
    /// cash-out, so it must stay screened too), for the periodic re-screen.
    ///
    /// <para>Unpaged deliberately. This set is one row per merchant per chain — hundreds, not millions —
    /// and the caller screens every one of them anyway, so paging would add a cursor to maintain and a
    /// class of bug (a wallet skipped because it moved between pages) in exchange for nothing.</para>
    /// </summary>
    Task<IReadOnlyList<SettlementWalletRef>> ListAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>One whitelisted cash-out destination, identified by the merchant that owns it.</summary>
public sealed record SettlementWalletRef(Guid MerchantId, Chain Chain, string Address);

/// <summary>An approved settlement wallet resolved for a cash-out. <see cref="IsActive"/> distinguishes the primary
/// (active) wallet from an approved standby; <see cref="ScreeningDecision"/> is its latest stored AML verdict
/// (Allow / Review / Block / Unavailable, null if never screened).</summary>
public sealed record ApprovedSettlementWallet(Guid WalletId, string Address, bool IsActive, string? ScreeningDecision);
