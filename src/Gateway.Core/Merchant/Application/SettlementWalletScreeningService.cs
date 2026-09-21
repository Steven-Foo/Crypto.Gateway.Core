using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Application;

/// <summary>
/// Keeps the screening verdict on every whitelisted settlement wallet from going stale.
///
/// <para><b>The problem it solves.</b> Whitelisting screens an address once, at the moment a staff member
/// approves it. A risk verdict is a snapshot, not a standing fact: an address clean on the day it was
/// approved can be designated months later. Without this, nothing would ever look again — while every one
/// of that merchant's earnings continues to be paid to it.</para>
///
/// <para><b>It flags; it never revokes.</b> A worsened verdict records evidence and logs a warning. It does
/// NOT remove the wallet, and that restraint is the design, not an omission:</para>
/// <list type="bullet">
/// <item>Revoking halts a merchant's cash-outs entirely. Doing that automatically, with no human in the
/// loop, hands a third party's opinion — or its outage — the power to stop a merchant being paid. That is
/// the exact failure the module refuses elsewhere by keeping <c>Unavailable</c> a separate outcome.</item>
/// <item>There is already a human in front of the money. Every merchant cash-out stops at
/// <c>PendingAdminAudit</c> for staff, so a flagged wallet is caught before funds move, by someone who can
/// weigh context the provider does not have.</item>
/// <item>A revocation is destructive and needs re-approval to undo. A flag costs nothing to ignore and
/// nothing to act on.</item>
/// </list>
///
/// <para><b>Cost.</b> A pass calls <see cref="IAddressScreeningService.ScreenAsync"/> for every wallet, and
/// that method serves any still-fresh verdict from its own cache without contacting the provider. So the
/// pass interval does not drive quota consumption — <c>Compliance:CacheDays</c> does. Running often is
/// cheap and simply shortens the lag between a verdict expiring and being refreshed.</para>
///
/// <para>No ledger impact and no keys (§10): this reads a public address and stores an opinion.</para>
/// </summary>
public sealed class SettlementWalletScreeningService(
    IMerchantSettlementDirectory settlementWallets,
    IAddressScreeningService? screening,
    IOptions<MerchantScreeningOptions> options,
    ILogger<SettlementWalletScreeningService> logger)
{
    private readonly MerchantScreeningOptions _options = options.Value;

    /// <summary>Re-screens every settlement wallet whose verdict has expired. Returns what it found, so a
    /// worker can log a single line rather than one per wallet.</summary>
    public async Task<SettlementRescreenResult> RescreenOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.RescreenSettlementWallets)
        {
            return SettlementRescreenResult.Skipped;
        }

        if (screening is null)
        {
            // Loud rather than silent, for the same reason the whitelisting path is: a control that is
            // configured on but not composed looks identical to one that is working, and the difference
            // only surfaces the day it was supposed to catch something.
            logger.LogError(
                "Settlement-wallet re-screening is enabled but no screening provider is composed in this host. "
                + "No wallet is being re-checked.");
            return SettlementRescreenResult.Skipped;
        }

        var wallets = await settlementWallets.ListAllAsync(cancellationToken);

        var screened = 0;
        var flagged = 0;

        foreach (var wallet in wallets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Read the standing verdict BEFORE re-screening, so a change can be reported as a change. The
            // decision alone is not enough to know something happened: a wallet that was already Block and
            // still is needs no new alarm, while Allow becoming Block does.
            var previous = await screening.FindLatestAsync(wallet.Chain, wallet.Address, cancellationToken);

            var verdict = await screening.ScreenAsync(
                wallet.Chain, wallet.Address, ScreeningPurpose.SettlementWallet, cancellationToken);

            if (verdict.FromCache)
            {
                // Still fresh, so no provider call was made and nothing can have changed.
                continue;
            }

            screened++;

            if (!HasWorsened(previous?.Decision, verdict.Decision))
            {
                continue;
            }

            flagged++;

            logger.LogWarning(
                "Settlement wallet for merchant {MerchantId} on {Chain} changed from {Previous} to {Current} "
                + "(score {Score}, {Level}): {Reasons}. The wallet is UNCHANGED and cash-outs still route to it "
                + "— a staff member must decide, and every cash-out already stops for audit. Evidence {ScreeningId}.",
                wallet.MerchantId, wallet.Chain, previous?.Decision.ToString() ?? "unscreened", verdict.Decision,
                verdict.Score, verdict.RiskLevel, string.Join(", ", verdict.Reasons), verdict.ScreeningId);
        }

        return new SettlementRescreenResult(wallets.Count, screened, flagged);
    }

    /// <summary>
    /// Whether the new decision is worse than the old one, on the ordering Allow &lt; Review &lt; Block.
    ///
    /// <para><c>Unavailable</c> is deliberately NOT on that scale and never counts as worsening. A provider
    /// outage is not news about the address, and treating it as a downgrade would fill the log with alarms
    /// every time the vendor had a bad afternoon — which is how a real alert gets ignored.</para>
    /// </summary>
    private static bool HasWorsened(ScreeningDecision? previous, ScreeningDecision current)
    {
        if (current is ScreeningDecision.Unavailable)
        {
            return false;
        }

        // A first-ever verdict is only notable if it is itself bad; a clean first result is the normal case.
        if (previous is null or ScreeningDecision.Unavailable)
        {
            return current is not ScreeningDecision.Allow;
        }

        return Rank(current) > Rank(previous.Value);
    }

    private static int Rank(ScreeningDecision decision) => decision switch
    {
        ScreeningDecision.Allow => 0,
        ScreeningDecision.Review => 1,
        ScreeningDecision.Block => 2,
        _ => -1,
    };
}

/// <summary>
/// What one re-screen pass did. <paramref name="Screened"/> counts only the wallets that actually cost a
/// provider call — the rest were served from a still-fresh verdict — so it is the number to watch against
/// quota.
/// </summary>
public sealed record SettlementRescreenResult(int Total, int Screened, int Flagged)
{
    public static SettlementRescreenResult Skipped { get; } = new(0, 0, 0);
}
