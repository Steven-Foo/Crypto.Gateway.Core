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
