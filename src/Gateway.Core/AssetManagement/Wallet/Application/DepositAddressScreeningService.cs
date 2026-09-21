using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application;

/// <summary>
/// Screens the platform's OWN receiving addresses for contamination.
///
/// <para><b>Why this shape, and not sender screening.</b> An inbound transfer cannot be screened while it is
/// in flight — there is nothing to screen until it lands, and by then it is a fact. It cannot be refused
/// either: an arrived deposit is credited, and a frozen merchant's deposits still credit the ledger (§14),
/// because freezing stops issuing and withdrawing but never recording. What IS available is the other side
/// of the same graph: a risk provider scores an address from its transaction history, so funds arriving
/// from a bad counterparty raise the score of OUR address. Watching the addresses we issue is therefore the
/// only honest inbound control, and it works after the fact by design rather than by accident.</para>
///
/// <para><b>It flags and nothing else.</b> No deposit is reversed, no credit withheld, no wallet disabled.
/// A flag tells staff which address received something worth investigating. Same restraint as the
/// settlement-wallet pass, for the stronger reason that here the money has already been credited to a
/// merchant, so an automatic reaction would be taking back funds on a vendor's say-so.</para>
///
/// <para><b>Only funded addresses are candidates.</b> A provisioned address that has never received
/// anything has no transaction graph, so screening it spends a call to be told nothing. The directory
/// already filters to addresses that have received at least one deposit.</para>
///
/// <para><b>The per-pass cap is the important control.</b> Deposit addresses are the one set here that
/// grows without bound — one per merchant, and more as volume grows — so an uncapped pass could consume a
/// day's quota in one sweep and starve the payout gate, which is the control that actually holds money.
/// The cap bounds a pass before it starts; addresses not reached this time are picked up next time, because
/// the candidate filter returns whatever still lacks a fresh verdict.</para>
///
/// <para>No ledger impact, no keys (§10) — public addresses and a third party's opinion of them.</para>
/// </summary>
public sealed class DepositAddressScreeningService(
    IWalletDirectory wallets,
    IAddressScreeningService? screening,
    IOptions<DepositAddressScreeningOptions> options,
    ILogger<DepositAddressScreeningService> logger)
{
    private readonly DepositAddressScreeningOptions _options = options.Value;

    /// <summary>
    /// Screens one batch of deposit addresses that currently lack a fresh verdict.
    /// </summary>
    /// <param name="force">
    /// Set by the manual trigger. It bypasses the <b>enabled</b> switch so staff can run a sweep on demand
    /// without leaving the scheduled pass turned on — it does NOT bypass the per-pass cap, because a manual
    /// run spends exactly the same quota as a scheduled one and an operator clicking a button is no reason
    /// to let it consume a day's budget.
    /// </param>
    public async Task<DepositAddressScreenResult> ScreenOnceAsync(
        bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && !_options.Enabled)
        {
            return DepositAddressScreenResult.Skipped;
        }

        if (screening is null)
        {
            // Loud, not silent: a control that is switched on but not composed looks exactly like one that
            // is working, and the difference only shows on the day it was supposed to catch something.
            logger.LogError(
                "Deposit-address screening is enabled but no screening provider is composed in this host. "
                + "No address is being checked.");
            return DepositAddressScreenResult.Skipped;
        }

        var budget = Math.Max(1, _options.MaxAddressesPerPass);
        var screened = 0;
        var flagged = 0;
        var candidates = 0;

        foreach (var chain in _options.EffectiveChains)
        {
            if (budget <= 0)
            {
                break;
            }

            var addresses = await wallets.ListReceivingDepositAddressesAsync(chain, cancellationToken);
            if (addresses.Count == 0)
            {
                continue;
            }

            var byAddress = addresses
                .GroupBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().WalletId, StringComparer.OrdinalIgnoreCase);

            // One query rather than a round trip per address. Returns only what would actually cost a call.
            var due = await screening.FindAddressesNeedingScreeningAsync(
                chain, [.. byAddress.Keys], budget, cancellationToken);

            candidates += due.Count;

            foreach (var address in due)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var verdict = await screening.ScreenAsync(
                    chain, address, ScreeningPurpose.DepositAddress, cancellationToken);

                screened++;
                budget--;

                if (verdict.Decision is ScreeningDecision.Allow or ScreeningDecision.Unavailable)
                {
                    continue;
                }

                flagged++;

                logger.LogWarning(
                    "Deposit address {Address} on {Chain} (wallet {WalletId}) screened {Decision} "
                    + "(score {Score}, {Level}): {Reasons}. Funds received here have ALREADY been credited — "
                    + "this is for investigation, nothing has been reversed or withheld. Evidence {ScreeningId}.",
                    Mask(address), chain, byAddress[address], verdict.Decision, verdict.Score,
                    verdict.RiskLevel, string.Join(", ", verdict.Reasons), verdict.ScreeningId);

                if (budget <= 0)
                {
                    break;
                }
            }
        }

        return new DepositAddressScreenResult(candidates, screened, flagged);
    }

    /// <summary>Addresses are public, but a full one in a log line is still needlessly re-identifying, so
    /// logs carry only the ends (§10 keeps full PII out of logs). The evidence row holds it in full.</summary>
    private static string Mask(string address) =>
        address.Length <= 12 ? address : $"{address[..6]}…{address[^4..]}";
}

/// <summary>
/// What one pass did. <paramref name="Screened"/> is the number of provider calls spent, which is the figure
/// to watch against quota; <paramref name="Candidates"/> is how many were due, so the two diverging means the
/// per-pass cap is biting and the backlog is not clearing.
/// </summary>
public sealed record DepositAddressScreenResult(int Candidates, int Screened, int Flagged)
{
    public static DepositAddressScreenResult Skipped { get; } = new(0, 0, 0);
}

/// <summary>
/// Bound from <c>Wallet:Screening</c>. Separate from <c>Merchant:Screening</c> and
/// <c>Withdrawal:Screening</c> for the reason that split exists throughout: Compliance says how risky an
/// address is, and each consumer decides what that means for its own flow. This one decides nothing at all —
/// it only records — so it must be switchable without touching a control that holds money.
/// </summary>
public sealed class DepositAddressScreeningOptions
{
    public const string SectionName = "Wallet:Screening";

    /// <summary>Run the scheduled pass. Default <b>false</b>. Off does not disable the manual trigger, which
    /// is the point of having both: staff can sweep on demand without committing to a standing spend.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Hard ceiling on provider calls per pass. Deposit addresses are the one candidate set that grows
    /// without bound, and the payout gate shares this quota — so an uncapped sweep could starve the control
    /// that actually holds money. Whatever is not reached this pass is picked up on the next one.
    /// </summary>
    public int MaxAddressesPerPass { get; set; } = 100;

    /// <summary>How often the scheduled pass runs. Like the settlement pass, this is how often it LOOKS;
    /// <c>Compliance:CacheDays</c> decides how often an address is actually re-screened.</summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>Chains to sweep. Only chains the gateway settles on, and only ones the provider covers.</summary>
    public Chain[] Chains { get; set; } = [Chain.Tron];

    /// <summary>
    /// <see cref="Chains"/>, de-duplicated — and this is not cosmetic.
    ///
    /// <para>.NET binds a configuration array by ADDING to whatever the property already holds, so a
    /// settings file listing <c>["Tron"]</c> against a default of <c>[Tron]</c> produces <c>[Tron, Tron]</c>.
    /// Observed live: the sweep looped twice over the same chain and screened every address twice, doubling
    /// quota consumption for no extra information. The same binding behaviour was found earlier on
    /// <c>Compliance:AlwaysBlockIndicators</c>, where it was merely noisy; here it spends money.</para>
    ///
    /// <para>Any array-typed option in this codebase has this behaviour. De-duplicate at the point of use.</para>
    /// </summary>
    internal IReadOnlyList<Chain> EffectiveChains => [.. Chains.Distinct()];
}
