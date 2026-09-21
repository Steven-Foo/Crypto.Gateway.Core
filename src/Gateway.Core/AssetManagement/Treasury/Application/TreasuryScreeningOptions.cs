namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;

/// <summary>
/// Whether registering a cold collection wallet screens the address, bound from <c>Treasury:Screening</c>.
///
/// <para>Separate from <c>Compliance</c>, <c>Withdrawal:Screening</c>, <c>Merchant:Screening</c> and
/// <c>Sweep:Screening</c> for the reason every one of those is separate: Compliance answers "how risky is
/// this address" and each consumer answers "what do I do about it". Here the answer is always "record it and
/// tell the operator" — a verdict never refuses a collection wallet — so it must be switchable without
/// touching the flags on the paths where a verdict does hold money.</para>
/// </summary>
public sealed class TreasuryScreeningOptions
{
    public const string SectionName = "Treasury:Screening";

    /// <summary>
    /// Screen a cold collection address when it is registered, and record the verdict on the row.
    ///
    /// <para>Default <b>true</b>, unlike the other screening switches, because this one cannot refuse
    /// anything: the cost of it being on is one provider call per address an operator adds — a handful a
    /// year — and the cost of it being off is the platform's largest custody address having no recorded
    /// verdict at all. With <c>Compliance:Enabled</c> false it simply records "unavailable".</para>
    /// </summary>
    public bool ScreenCollectionWallets { get; set; } = true;
}
