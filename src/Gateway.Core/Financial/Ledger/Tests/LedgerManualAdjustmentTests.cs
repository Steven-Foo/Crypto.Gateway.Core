using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Tests;

/// <summary>
/// Staff-initiated manual credit/debit (a compensating entry, §14 — never an edit to history). The
/// money-critical point: the counterparty is <see cref="AccountType.ManualAdjustmentCredit"/>/
/// <see cref="AccountType.ManualAdjustmentDebit"/>, never <see cref="AccountType.TreasuryAsset"/> — a manual
/// correction must not move the figure Reconciliation compares against real on-chain balances, or it would
/// create a permanent, false drift.
/// </summary>
public sealed class LedgerManualAdjustmentTests : LedgerTestHost
{
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly BigInteger Amount = BigInteger.Parse("500000");

    private static async Task<BigInteger> MerchantBalanceAsync(LedgerDbContext ctx, AccountType type) =>
        await ctx.AccountBalances
            .Join(ctx.Accounts, b => b.Id, a => a.Id, (b, a) => new { b, a })
            .Where(x => x.a.AccountType == type && x.a.OwnerId == Merchant && x.a.AssetId == Asset)
            .Select(x => x.b.Balance)
            .SingleOrDefaultAsync(Ct);

    private static async Task<BigInteger> SystemBalanceAsync(LedgerDbContext ctx, AccountType type) =>
        await ctx.AccountBalances
            .Join(ctx.Accounts, b => b.Id, a => a.Id, (b, a) => new { b, a })
            .Where(x => x.a.AccountType == type && x.a.OwnerId == null && x.a.AssetId == Asset)
            .Select(x => x.b.Balance)
            .SingleOrDefaultAsync(Ct);

    [Fact]
    public async Task A_manual_credit_debits_ManualAdjustment_and_credits_the_merchant_never_TreasuryAsset()
    {
        await using (var ctx = Context())
            (await Poster(ctx).CreditMerchantBalanceAsync(
                new CreditMerchantBalanceCommand(Merchant, Asset, Amount, "Support ticket #1234"), Ct))
                .Value.ShouldBe(PostingOutcome.Posted);

        await using var verify = Context();
        (await MerchantBalanceAsync(verify, AccountType.MerchantLiability)).ShouldBe(Amount);
        (await SystemBalanceAsync(verify, AccountType.ManualAdjustmentCredit)).ShouldBe(Amount);
        // The money-critical assertion: TreasuryAsset — what Reconciliation compares against real on-chain
        // balances — must be completely untouched by a manual (non-on-chain) adjustment.
        (await SystemBalanceAsync(verify, AccountType.TreasuryAsset)).ShouldBe(BigInteger.Zero);
    }

    [Fact]
    public async Task A_manual_debit_debits_the_merchant_and_credits_ManualAdjustment()
    {
        await using (var ctx = Context())
            await Poster(ctx).CreditMerchantBalanceAsync(new CreditMerchantBalanceCommand(Merchant, Asset, Amount, "seed"), Ct);

        var debitAmount = BigInteger.Parse("200000");
        await using (var ctx = Context())
            (await Poster(ctx).DebitMerchantBalanceAsync(
                new DebitMerchantBalanceCommand(Merchant, Asset, debitAmount, "Correcting a duplicate credit"), Ct))
                .Value.ShouldBe(PostingOutcome.Posted);

        await using var verify = Context();
        (await MerchantBalanceAsync(verify, AccountType.MerchantLiability)).ShouldBe(Amount - debitAmount);
        (await SystemBalanceAsync(verify, AccountType.ManualAdjustmentDebit)).ShouldBe(debitAmount);
        // The credit-side bucket is untouched by a debit — the two are independent, one-directional trackers.
        (await SystemBalanceAsync(verify, AccountType.ManualAdjustmentCredit)).ShouldBe(Amount);
    }

    [Fact]
    public async Task A_debit_larger_than_the_balance_is_rejected_and_changes_nothing()
    {
        await using (var ctx = Context())
            await Poster(ctx).CreditMerchantBalanceAsync(new CreditMerchantBalanceCommand(Merchant, Asset, Amount, "seed"), Ct);

        await using (var ctx = Context())
        {
            var result = await Poster(ctx).DebitMerchantBalanceAsync(
                new DebitMerchantBalanceCommand(Merchant, Asset, Amount + BigInteger.One, "Too much"), Ct);

            result.IsFailure.ShouldBeTrue();
            result.Error!.Code.ShouldBe(LedgerErrors.InsufficientBalanceForAdjustment.Code);
        }

        await using var verify = Context();
        (await MerchantBalanceAsync(verify, AccountType.MerchantLiability)).ShouldBe(Amount); // unchanged
    }

    [Fact]
    public async Task Replaying_the_same_adjustment_id_never_double_posts()
    {
        var adjustmentId = Guid.CreateVersion7();
        var command = new CreditMerchantBalanceCommand(Merchant, Asset, Amount, "Retried staff action", adjustmentId);

        await using (var ctx = Context())
            (await Poster(ctx).CreditMerchantBalanceAsync(command, Ct)).Value.ShouldBe(PostingOutcome.Posted);
        await using (var ctx = Context())
            (await Poster(ctx).CreditMerchantBalanceAsync(command, Ct)).Value.ShouldBe(PostingOutcome.AlreadyPosted);

        await using var verify = Context();
        (await MerchantBalanceAsync(verify, AccountType.MerchantLiability)).ShouldBe(Amount); // not doubled
    }

    [Fact]
    public async Task A_credit_and_a_debit_with_no_caller_supplied_id_each_get_their_own_distinct_journal()
    {
        // Two calls with NO AdjustmentId must each mint their own — never collide with each other via a
        // shared default, which would silently drop the second one as "AlreadyPosted".
        await using (var ctx = Context())
            (await Poster(ctx).CreditMerchantBalanceAsync(new CreditMerchantBalanceCommand(Merchant, Asset, Amount, "first"), Ct))
                .Value.ShouldBe(PostingOutcome.Posted);
        await using (var ctx = Context())
            (await Poster(ctx).CreditMerchantBalanceAsync(new CreditMerchantBalanceCommand(Merchant, Asset, Amount, "second"), Ct))
                .Value.ShouldBe(PostingOutcome.Posted);

        await using var verify = Context();
        (await MerchantBalanceAsync(verify, AccountType.MerchantLiability)).ShouldBe(Amount * 2);
    }

    [Fact]
    public async Task A_non_positive_amount_is_rejected()
    {
        await using var ctx = Context();
        var result = await Poster(ctx).CreditMerchantBalanceAsync(
            new CreditMerchantBalanceCommand(Merchant, Asset, BigInteger.Zero, "bad"), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(LedgerErrors.NonPositiveAmount.Code);
    }
}
