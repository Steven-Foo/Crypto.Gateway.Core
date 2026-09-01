using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Tests;

/// <summary>
/// The two postings that exist because some money moves OUTSIDE platform custody: an admin topping up a hot
/// withdrawal wallet from company funds, and an admin paying a merchant settlement from a company wallet.
///
/// <para>These tests exist for one reason: each posting has exactly one plausible-but-wrong counterparty
/// account, and picking it would corrupt the books silently — no exception, no failed journal, just a number
/// that drifts a little further from reality with every transaction. So the assertions are deliberately about
/// what must <em>not</em> move, as much as what must.</para>
/// </summary>
public sealed class LedgerExternalSettlementTests : LedgerTestHost
{
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly BigInteger Hundred = BigInteger.Parse("100000000"); // 100 USDT @ 6dp
    private static readonly BigInteger Fifty = BigInteger.Parse("50000000");
    private static readonly BigInteger Twenty = BigInteger.Parse("20000000");

    private static async Task<BigInteger> BalanceAsync(LedgerDbContext ctx, AccountType type, Guid? ownerId = null) =>
        await ctx.AccountBalances
            .Join(ctx.Accounts, b => b.Id, a => a.Id, (b, a) => new { b, a })
            .Where(x => x.a.AccountType == type && x.a.OwnerId == ownerId && x.a.AssetId == Asset)
            .Select(x => x.b.Balance)
            .SingleOrDefaultAsync(Ct);

    /// <summary>Credits the merchant so there is something to reserve against, exactly as a confirmed deposit does.</summary>
    private async Task GivenDepositedAsync(BigInteger amount)
    {
        await using var ctx = Context();
        (await Poster(ctx).CreditDepositAsync(
            new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, amount), Ct)).Value.ShouldBe(PostingOutcome.Posted);
    }

    /// <summary>Locks the funds exactly as a withdrawal request does — reserve is on the IWithdrawalLedger
    /// face of the same poster, which is why this casts rather than using the ILedgerPoster helper.</summary>
    private async Task GivenReservedAsync(Guid withdrawalId, BigInteger amount)
    {
        await using var ctx = Context();
        var ledger = (IWithdrawalLedger)Poster(ctx);
        (await ledger.ReserveAsync(
            new ReserveWithdrawalRequest(withdrawalId, Merchant, Asset, amount, BigInteger.Zero), Ct))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_top_up_raises_custody_and_never_touches_merchant_money()
    {
        await GivenDepositedAsync(Hundred);

        await using (var ctx = Context())
            (await Poster(ctx).RecordTopUpAsync(new RecordTopUpCommand(Guid.CreateVersion7(), Asset, Fifty), Ct))
                .Value.ShouldBe(PostingOutcome.Posted);

        await using var verify = Context();

        // Custody rises, because we really are holding 50 more in a watched address. If this did not move,
        // reconciliation would report drift equal to every top-up ever recorded.
        (await BalanceAsync(verify, AccountType.TreasuryAsset)).ShouldBe(Hundred + Fifty);
        (await BalanceAsync(verify, AccountType.WithdrawalWalletTopUp)).ShouldBe(Fifty);

        // THE INVARIANT: operating liquidity is not merchant money. A top-up must never inflate what a
        // merchant can withdraw — otherwise the platform would be handing out float as if it were earnings.
        (await BalanceAsync(verify, AccountType.MerchantLiability, Merchant)).ShouldBe(Hundred);
        (await BalanceAsync(verify, AccountType.FeeRevenue)).ShouldBe(BigInteger.Zero);
    }

    [Fact]
    public async Task An_externally_settled_withdrawal_discharges_the_reserve_without_reducing_custody()
    {
        await GivenDepositedAsync(Hundred);
        var withdrawalId = Guid.CreateVersion7();
        await GivenReservedAsync(withdrawalId, Twenty);

        await using (var ctx = Context())
            (await Poster(ctx).SettleWithdrawalAsync(
                new SettleWithdrawalCommand(withdrawalId, Merchant, Asset, Twenty, BigInteger.Zero, ExternallySettled: true), Ct))
                .Value.ShouldBe(PostingOutcome.Posted);

        await using var verify = Context();

        // THE INVARIANT: an admin paid this from a company wallet outside platform custody, so no watched
        // address was debited. Crediting TreasuryAsset here would decrement custody that never left, and
        // reconciliation would drift downward by every settlement ever made.
        (await BalanceAsync(verify, AccountType.TreasuryAsset)).ShouldBe(Hundred);
        (await BalanceAsync(verify, AccountType.ExternalSettlement)).ShouldBe(Twenty);

        // The merchant side is unchanged from any other settlement: the reserve is discharged, and the
        // merchant's available balance stays where the reserve already left it.
        (await BalanceAsync(verify, AccountType.WithdrawalClearing)).ShouldBe(BigInteger.Zero);
        (await BalanceAsync(verify, AccountType.MerchantLiability, Merchant)).ShouldBe(Hundred - Twenty);
    }

    [Fact]
    public async Task An_on_platform_withdrawal_still_reduces_custody()
    {
        await GivenDepositedAsync(Hundred);
        var withdrawalId = Guid.CreateVersion7();
        await GivenReservedAsync(withdrawalId, Twenty);

        // The default (and every automated payout): our own hot wallet paid, so custody genuinely falls.
        await using (var ctx = Context())
            (await Poster(ctx).SettleWithdrawalAsync(
                new SettleWithdrawalCommand(withdrawalId, Merchant, Asset, Twenty, BigInteger.Zero), Ct))
                .Value.ShouldBe(PostingOutcome.Posted);

        await using var verify = Context();
        (await BalanceAsync(verify, AccountType.TreasuryAsset)).ShouldBe(Hundred - Twenty);
        (await BalanceAsync(verify, AccountType.ExternalSettlement)).ShouldBe(BigInteger.Zero);
    }

    /// <summary>
    /// The whole model end to end, on the numbers the flow was designed against: deposit 100, top up 50,
    /// pay a user payout of 30 from our own wallet, and settle a merchant cash-out of 20 externally.
    /// </summary>
    [Fact]
    public async Task The_full_flow_leaves_custody_and_merchant_balance_both_correct()
    {
        await GivenDepositedAsync(Hundred);

        await using (var ctx = Context())
            await Poster(ctx).RecordTopUpAsync(new RecordTopUpCommand(Guid.CreateVersion7(), Asset, Fifty), Ct);

        var payout = Guid.CreateVersion7();
        var thirty = BigInteger.Parse("30000000");
        await GivenReservedAsync(payout, thirty);
        await using (var ctx = Context())
            await Poster(ctx).SettleWithdrawalAsync(new SettleWithdrawalCommand(payout, Merchant, Asset, thirty, BigInteger.Zero), Ct);

        var cashOut = Guid.CreateVersion7();
        await GivenReservedAsync(cashOut, Twenty);
        await using (var ctx = Context())
            await Poster(ctx).SettleWithdrawalAsync(
                new SettleWithdrawalCommand(cashOut, Merchant, Asset, Twenty, BigInteger.Zero, ExternallySettled: true), Ct);

        await using var verify = Context();

        // Custody = deposits + top-ups − payouts paid from our own wallets. The external settlement is
        // deliberately absent: it never crossed the custody boundary.
        (await BalanceAsync(verify, AccountType.TreasuryAsset)).ShouldBe(Hundred + Fifty - thirty);

        // Merchant withdrawable = deposits − fees − payouts − settlements. The top-up appears nowhere in it.
        (await BalanceAsync(verify, AccountType.MerchantLiability, Merchant)).ShouldBe(Hundred - thirty - Twenty);

        // Nothing stranded in clearing, and every journal balanced. Summed client-side: this project's money
        // mapping has no SQL SUM translation for BigInteger (§14), and the row count here is trivial.
        (await BalanceAsync(verify, AccountType.WithdrawalClearing)).ShouldBe(BigInteger.Zero);
        var entries = await verify.JournalEntries.AsNoTracking().Select(e => new { e.Debit, e.Credit }).ToListAsync(Ct);
        entries.Aggregate(BigInteger.Zero, (sum, e) => sum + e.Debit)
            .ShouldBe(entries.Aggregate(BigInteger.Zero, (sum, e) => sum + e.Credit));
    }

    [Fact]
    public async Task Re_recording_the_same_top_up_is_idempotent()
    {
        var topUpId = Guid.CreateVersion7();
        var command = new RecordTopUpCommand(topUpId, Asset, Fifty);

        await using (var ctx = Context())
            (await Poster(ctx).RecordTopUpAsync(command, Ct)).Value.ShouldBe(PostingOutcome.Posted);
        await using (var ctx = Context())
            (await Poster(ctx).RecordTopUpAsync(command, Ct)).Value.ShouldBe(PostingOutcome.AlreadyPosted);

        await using var verify = Context();
        (await BalanceAsync(verify, AccountType.TreasuryAsset)).ShouldBe(Fifty); // not double-counted
    }

    /// <summary>
    /// The four System accounts that arrived from two independently developed features — a top-up and an
    /// external settlement (this branch) alongside a manual credit and a manual debit (the merged branch) —
    /// collided on the same AccountType enum numbers and were renumbered to coexist.
    ///
    /// <para>The risk that renumbering creates is invisible: if two AccountTypes resolved to the same account
    /// row, postings would silently pool into one bucket and every figure derived from them — custody,
    /// settlements paid, manual corrections — would be wrong together, with the journals still balancing
    /// perfectly. So this asserts the four buckets stay genuinely separate under all four postings, and that
    /// the merchant-facing invariant still holds with all of them in play.</para>
    /// </summary>
    [Fact]
    public async Task Top_ups_settlements_and_manual_adjustments_never_share_an_account()
    {
        var ten = BigInteger.Parse("10000000");
        await GivenDepositedAsync(Hundred);

        await using (var ctx = Context())
            await Poster(ctx).RecordTopUpAsync(new RecordTopUpCommand(Guid.CreateVersion7(), Asset, Fifty), Ct);
        await using (var ctx = Context())
            await Poster(ctx).CreditMerchantBalanceAsync(
                new CreditMerchantBalanceCommand(Merchant, Asset, ten, "goodwill"), Ct);
        await using (var ctx = Context())
            await Poster(ctx).DebitMerchantBalanceAsync(
                new DebitMerchantBalanceCommand(Merchant, Asset, ten, "clawback"), Ct);

        await using var verify = Context();

        // Each bucket holds exactly its own postings — no bleed between the four.
        (await BalanceAsync(verify, AccountType.WithdrawalWalletTopUp)).ShouldBe(Fifty);
        (await BalanceAsync(verify, AccountType.ManualAdjustmentCredit)).ShouldBe(ten);
        (await BalanceAsync(verify, AccountType.ManualAdjustmentDebit)).ShouldBe(ten);
        (await BalanceAsync(verify, AccountType.ExternalSettlement)).ShouldBe(BigInteger.Zero);

        // A top-up raises custody; the two manual adjustments must not — they are corrections, never on-chain
        // movements, so TreasuryAsset (what Reconciliation compares to real balances) sees only the top-up.
        (await BalanceAsync(verify, AccountType.TreasuryAsset)).ShouldBe(Hundred + Fifty);

        // The manual credit and debit cancel, so the merchant is left with exactly their deposit.
        (await BalanceAsync(verify, AccountType.MerchantLiability, Merchant)).ShouldBe(Hundred);

        var entries = await verify.JournalEntries.AsNoTracking().Select(e => new { e.Debit, e.Credit }).ToListAsync(Ct);
        entries.Aggregate(BigInteger.Zero, (sum, e) => sum + e.Debit)
            .ShouldBe(entries.Aggregate(BigInteger.Zero, (sum, e) => sum + e.Credit));
    }
}
