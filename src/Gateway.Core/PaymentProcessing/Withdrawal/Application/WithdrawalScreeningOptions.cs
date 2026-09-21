namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;

/// <summary>
/// What the payout flow does with a screening verdict, bound from <c>Withdrawal:Screening</c>.
///
/// <para>Deliberately separate from <c>Compliance</c>'s own settings, and that split is the point: Compliance
/// answers "how risky is this address", the payout flow answers "what do I do about it". Keeping the second
/// question here means a second consumer (deposit senders, settlement wallets) can answer it differently
/// without renegotiating a shared policy.</para>
/// </summary>
public sealed class WithdrawalScreeningOptions
{
    public const string SectionName = "Withdrawal:Screening";

    /// <summary>
    /// Master switch. Off ⇒ payouts route exactly as they did before this feature existed, never entering
    /// <c>PendingScreening</c>. Default <b>false</b>, so screening is something an operator turns on
    /// deliberately once the provider is proven, never something that silently starts holding money.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// What to do when no verdict could be obtained — provider down, quota spent, plan lapsed.
    ///
    /// <para><b>Hold</b> (default) sends the payout to the platform approval queue so a human decides. It is
    /// the safe reading: an unknown is not an endorsement. The cost is that a prolonged vendor outage grows a
    /// staff queue rather than stopping payouts outright — deliberately, since a hold is recoverable by a
    /// human and an auto-refusal is not.</para>
    ///
    /// <para><b>Allow</b> lets the payout continue as if clean. Choose it only with the trade-off understood:
    /// it keeps money moving during an outage at the price of an unscreened payout, which is exactly the
    /// exposure screening exists to prevent. It is never the default.</para>
    /// </summary>
    public ScreeningUnavailableBehaviour OnUnavailable { get; set; } = ScreeningUnavailableBehaviour.Hold;
}

/// <summary>What an inconclusive screening does to a payout.</summary>
public enum ScreeningUnavailableBehaviour
{
    /// <summary>Route to the platform approval queue for a human decision. The safe default.</summary>
    Hold = 0,

    /// <summary>Treat as clean and continue. Keeps payouts moving during a vendor outage, unscreened.</summary>
    Allow = 1
}
