namespace CryptoPaymentEngine.Gateway.Core.Merchant.Application;

/// <summary>
/// Whether staff actions in this module screen an address before accepting it, bound from
/// <c>Merchant:Screening</c>.
///
/// <para>Separate from both <c>Compliance</c> and <c>Withdrawal:Screening</c> on purpose, following the same
/// split: Compliance answers "how risky is this address", and each consumer answers "what do I do about it"
/// for its own flow. Settlement whitelisting and payout sending genuinely want different answers — see
/// <c>MerchantRegistrar.SetSettlementWalletAsync</c> for why — so they must be switchable independently.</para>
/// </summary>
public sealed class MerchantScreeningOptions
{
    public const string SectionName = "Merchant:Screening";

    /// <summary>
    /// Screen a settlement wallet before whitelisting it. Default <b>false</b>: screening is switched on
    /// deliberately. Off means no provider call at all, rather than a call that reports "unknown" — which
    /// would otherwise attach a meaningless warning to every settlement-wallet save.
    /// </summary>
    public bool ScreenSettlementWallets { get; set; }

    /// <summary>
    /// Refuse to make a directly designated address (<c>Block</c>) the cash-out destination. Default
    /// <b>true</b>.
    ///
    /// <para>Whitelisting itself is never refused — staff keep the address on file, and the verdict with it.
    /// This governs only the act that moves money: every one of that merchant's earnings is paid to the
    /// active address, and a sanctions designation is a legal fact rather than a risk appetite. Set false
    /// where the business accepts that judgement resting entirely with the operator; the verdict is still
    /// recorded and returned either way.</para>
    /// </summary>
    public bool BlockActivationOnScreeningBlock { get; set; } = true;

    /// <summary>
    /// Periodically re-screen settlement wallets already on file. Default <b>false</b>.
    ///
    /// <para><b>Why this is needed at all.</b> Whitelisting screens an address once. A verdict is a snapshot,
    /// not a standing fact — an address clean on the day it was approved can be designated months later, and
    /// nothing would ever notice. Every one of that merchant's earnings goes to that address in the meantime.</para>
    ///
    /// <para>It is separate from <see cref="ScreenSettlementWallets"/> because the two spend quota very
    /// differently: whitelisting is a handful of calls a month, while this is one call per merchant per chain
    /// per cycle, forever. Turning on the first should not silently commit you to the second.</para>
    /// </summary>
    public bool RescreenSettlementWallets { get; set; }

    /// <summary>
    /// How often a re-screen pass runs. Note this is NOT how often an address is actually re-screened —
    /// <c>Compliance:CacheDays</c> decides that, because a pass reuses any verdict still fresh. A short
    /// interval therefore costs nothing extra; it only shortens the lag between a verdict expiring and it
    /// being refreshed. Twelve hours means a stale wallet is picked up within half a day.
    /// </summary>
    public int RescreenIntervalHours { get; set; } = 12;
}
