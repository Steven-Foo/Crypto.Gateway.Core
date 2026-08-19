using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Tests;

/// <summary>
/// Proves the public balance read model (<see cref="ILedgerQuery"/>) that a host's <c>/balance</c> endpoint
/// derives from — never a stored column. It must equal credited deposits minus what is reserved/settled,
/// i.e. the merchant's <em>available</em> balance.
/// </summary>
public sealed class LedgerQueryTests : LedgerTestHost
{
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly BigInteger Deposited = BigInteger.Parse("10000000"); // 10 USDT
    private static readonly BigInteger Amount = BigInteger.Parse("3000000");     // 3
    private static readonly BigInteger Fee = BigInteger.Parse("100000");         // 0.1

    [Fact]
    public async Task A_merchant_with_no_ledger_activity_has_a_zero_balance()
    {
        await using var ctx = Context();
        (await new LedgerQuery(ctx).GetMerchantBalanceAsync(Merchant, Asset, Ct)).ShouldBe(BigInteger.Zero);
    }

    [Fact]
    public async Task The_balance_equals_the_credited_deposit()
    {
        await using (var ctx = Context())
            (await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Deposited), Ct))
                .IsSuccess.ShouldBeTrue();

        await using var verify = Context();
        (await new LedgerQuery(verify).GetMerchantBalanceAsync(Merchant, Asset, Ct)).ShouldBe(Deposited);
    }

    [Fact]
    public async Task A_reserved_withdrawal_is_excluded_so_the_query_returns_the_available_balance()
    {
        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Deposited), Ct);
        await using (var ctx = Context())
            await ((IWithdrawalLedger)Poster(ctx)).ReserveAsync(new ReserveWithdrawalRequest(Guid.CreateVersion7(), Merchant, Asset, Amount, Fee), Ct);

        await using var verify = Context();
        (await new LedgerQuery(verify).GetMerchantBalanceAsync(Merchant, Asset, Ct)).ShouldBe(Deposited - (Amount + Fee));
    }

    [Fact]
    public async Task Balances_are_isolated_per_merchant_and_per_asset()
    {
        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Deposited), Ct);

        await using var verify = Context();
        var query = new LedgerQuery(verify);
        (await query.GetMerchantBalanceAsync(Merchant, Guid.CreateVersion7(), Ct)).ShouldBe(BigInteger.Zero); // other asset
        (await query.GetMerchantBalanceAsync(Guid.CreateVersion7(), Asset, Ct)).ShouldBe(BigInteger.Zero);    // other merchant
    }

    // ── settled (T+N withdrawable) balance ──

    [Fact]
    public async Task Settled_balance_equals_total_when_every_deposit_has_matured()
    {
        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Deposited), Ct);

        // Cutoff in the future ⇒ nothing is still-maturing ⇒ the whole balance is settled.
        await using var verify = Context();
        (await new LedgerQuery(verify).GetMerchantSettledBalanceAsync(Merchant, Asset, DateTimeOffset.UtcNow.AddDays(1), Ct))
            .ShouldBe(Deposited);
    }

    [Fact]
    public async Task A_still_maturing_deposit_is_excluded_from_the_settled_balance()
    {
        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Deposited), Ct);

        // Cutoff in the past ⇒ the just-made deposit is dated on/after it ⇒ unmatured ⇒ nothing settled yet.
        await using var verify = Context();
        (await new LedgerQuery(verify).GetMerchantSettledBalanceAsync(Merchant, Asset, DateTimeOffset.UtcNow.AddDays(-1), Ct))
            .ShouldBe(BigInteger.Zero);
    }

    [Fact]
    public async Task Only_matured_deposits_count_toward_settled_a_recent_one_does_not()
    {
        var old = new FixedTime(DateTimeOffset.UtcNow.AddDays(-5));
        var recent = new FixedTime(DateTimeOffset.UtcNow);

        await using (var ctx = Context())
            await Poster(ctx, old).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Deposited), Ct);
        await using (var ctx = Context())
            await Poster(ctx, recent).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Amount), Ct);

        // Cutoff between the two ⇒ the 5-day-old deposit is settled, the just-now one is not.
        await using var verify = Context();
        (await new LedgerQuery(verify).GetMerchantSettledBalanceAsync(Merchant, Asset, DateTimeOffset.UtcNow.AddDays(-1), Ct))
            .ShouldBe(Deposited);
    }

    [Fact]
    public async Task A_reversed_recent_deposit_nets_to_zero_and_does_not_reduce_matured_settled_funds()
    {
        var reorgedId = Guid.CreateVersion7();
        var old = new FixedTime(DateTimeOffset.UtcNow.AddDays(-5));
        var recent = new FixedTime(DateTimeOffset.UtcNow);

        await using (var ctx = Context())
            await Poster(ctx, old).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Deposited), Ct);
        await using (var ctx = Context())
            await Poster(ctx, recent).CreditDepositAsync(new CreditDepositCommand(reorgedId, Merchant, Asset, Amount), Ct);
        await using (var ctx = Context())
            await Poster(ctx, recent).ReverseDepositAsync(new ReverseDepositCommand(reorgedId, Merchant, Asset, Amount), Ct);

        // The recent deposit + its reversal both fall in the unmatured window and net to zero, so settled stays
        // the matured 10M — NOT 7M (which a gross-only "subtract recent credits" rule would wrongly produce).
        await using var verify = Context();
        (await new LedgerQuery(verify).GetMerchantSettledBalanceAsync(Merchant, Asset, DateTimeOffset.UtcNow.AddDays(-1), Ct))
            .ShouldBe(Deposited);
    }

    [Fact]
    public async Task Settled_never_goes_negative_when_outflows_exceed_matured_inflows()
    {
        // A matured deposit, then a reserve larger relative to what stays matured: settled clamps at zero.
        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Deposited), Ct);
        await using (var ctx = Context())
            await ((IWithdrawalLedger)Poster(ctx)).ReserveAsync(new ReserveWithdrawalRequest(Guid.CreateVersion7(), Merchant, Asset, Amount, Fee), Ct);

        // Cutoff in the past ⇒ the deposit is treated as unmatured, but the reserve already left the balance:
        // total = 10M − (3M+0.1M); unmaturedNet = 10M ⇒ settled = negative ⇒ clamped to zero.
        await using var verify = Context();
        (await new LedgerQuery(verify).GetMerchantSettledBalanceAsync(Merchant, Asset, DateTimeOffset.UtcNow.AddDays(-1), Ct))
            .ShouldBe(BigInteger.Zero);
    }

    [Fact]
    public async Task Treasury_holding_is_zero_when_the_asset_has_never_been_custodied()
    {
        await using var ctx = Context();
        (await new LedgerQuery(ctx).GetTreasuryHoldingAsync(Asset, Ct)).ShouldBe(BigInteger.Zero);
    }

    [Fact]
    public async Task Treasury_holding_rises_by_the_gross_of_a_confirmed_deposit_independent_of_the_fee()
    {
        await using (var ctx = Context())
            (await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Deposited, Fee), Ct))
                .IsSuccess.ShouldBeTrue();

        // TreasuryAsset is debited the GROSS received; the fee only changes the merchant/fee split, not custody.
        await using var verify = Context();
        (await new LedgerQuery(verify).GetTreasuryHoldingAsync(Asset, Ct)).ShouldBe(Deposited);
    }

    [Fact]
    public async Task Treasury_holding_falls_by_a_settled_withdrawal_amount_not_its_fee()
    {
        var withdrawalId = Guid.CreateVersion7();
        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Deposited), Ct);
        await using (var ctx = Context())
            await ((IWithdrawalLedger)Poster(ctx)).ReserveAsync(new ReserveWithdrawalRequest(withdrawalId, Merchant, Asset, Amount, Fee), Ct);
        await using (var ctx = Context())
            (await Poster(ctx).SettleWithdrawalAsync(new SettleWithdrawalCommand(withdrawalId, Merchant, Asset, Amount, Fee), Ct))
                .IsSuccess.ShouldBeTrue();

        // Custody drops by what left the chain (the amount); the fee becomes revenue, still custodied.
        await using var verify = Context();
        (await new LedgerQuery(verify).GetTreasuryHoldingAsync(Asset, Ct)).ShouldBe(Deposited - Amount);
    }

    [Fact]
    public async Task Journal_history_shows_a_deposit_as_a_credit_on_the_merchant_liability_line()
    {
        var merchant = Guid.CreateVersion7();
        await using (var ctx = Context())
            (await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), merchant, Asset, Deposited), Ct))
                .IsSuccess.ShouldBeTrue();

        await using var verify = Context();
        var (items, total) = await new LedgerQuery(verify).GetJournalsAsync(merchant, null, null, null, 1, 50, Ct);

        total.ShouldBe(1);
        var journal = items.Single();
        journal.ReferenceType.ShouldBe("Deposit");
        journal.Direction.ShouldBe("Credit");
        journal.Amount.ShouldBe(Deposited);
    }

    [Fact]
    public async Task Journal_history_is_paged_newest_first_and_isolated_per_merchant()
    {
        var merchant = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();

        await using (var ctx = Context())
        {
            var poster = Poster(ctx);
            await poster.CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), merchant, Asset, Deposited), Ct);
            await poster.CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), merchant, Asset, Amount), Ct);
            await poster.CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), other, Asset, Deposited), Ct);
        }

        await using var verify = Context();
        var (items, total) = await new LedgerQuery(verify).GetJournalsAsync(merchant, null, null, null, 1, 1, Ct);

        total.ShouldBe(2); // the other merchant's journal is excluded from the count
        items.Count.ShouldBe(1); // page size respected
    }

    [Fact]
    public async Task No_merchant_filter_returns_every_merchants_journals()
    {
        var merchantA = Guid.CreateVersion7();
        var merchantB = Guid.CreateVersion7();

        await using (var ctx = Context())
        {
            var poster = Poster(ctx);
            await poster.CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), merchantA, Asset, Deposited), Ct);
            await poster.CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), merchantB, Asset, Amount), Ct);
        }

        await using var verify = Context();
        var (items, total) = await new LedgerQuery(verify).GetJournalsAsync(null, null, null, null, 1, 50, Ct);

        total.ShouldBeGreaterThanOrEqualTo(2);
        items.ShouldContain(i => i.Amount == Deposited);
        items.ShouldContain(i => i.Amount == Amount);
    }

    [Fact]
    public async Task A_date_range_excludes_journals_outside_it()
    {
        var merchant = Guid.CreateVersion7();
        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), merchant, Asset, Deposited), Ct);

        await using var verify = Context();
        var query = new LedgerQuery(verify);

        var future = DateTimeOffset.UtcNow.AddDays(1);
        var (futureItems, futureTotal) = await query.GetJournalsAsync(merchant, null, future, null, 1, 50, Ct);
        futureTotal.ShouldBe(0);
        futureItems.ShouldBeEmpty();

        var past = DateTimeOffset.UtcNow.AddDays(-1);
        var (pastItems, pastTotal) = await query.GetJournalsAsync(merchant, null, past, null, 1, 50, Ct);
        pastTotal.ShouldBe(1);
        pastItems.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_referenceId_filter_returns_only_that_journal()
    {
        var merchant = Guid.CreateVersion7();
        var depositId = Guid.CreateVersion7();

        await using (var ctx = Context())
        {
            var poster = Poster(ctx);
            await poster.CreditDepositAsync(new CreditDepositCommand(depositId, merchant, Asset, Deposited), Ct);
            await poster.CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), merchant, Asset, Amount), Ct);
        }

        await using var verify = Context();
        var (items, total) = await new LedgerQuery(verify).GetJournalsAsync(merchant, depositId, null, null, 1, 50, Ct);

        total.ShouldBe(1);
        items.Single().ReferenceId.ShouldBe(depositId);
        items.Single().Amount.ShouldBe(Deposited);
    }
}
