using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Tests;

/// <summary>
/// <see cref="ILedgerQuery.GetMerchantBalanceHistoryAsync"/> — the "account statement" read. The
/// money-critical assertion: a settled withdrawal's <c>WithdrawalSettle</c> journal carries the merchant's
/// id but never posts a line against their liability account (the balance already moved at reserve time),
/// so it must be completely absent from this history — never a zero-amount row.
/// </summary>
public sealed class LedgerBalanceHistoryTests : LedgerTestHost
{
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly BigInteger Deposited = BigInteger.Parse("100000000"); // 100 USDT
    private static readonly BigInteger WithdrawAmount = BigInteger.Parse("3000000");

    private static ILedgerQuery Query(LedgerDbContext ctx) => new LedgerQuery(ctx);

    [Fact]
    public async Task History_includes_deposit_reserve_release_and_manual_lines_but_never_settle()
    {
        var settledWithdrawalId = Guid.CreateVersion7();
        var releasedWithdrawalId = Guid.CreateVersion7();

        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Deposited), Ct);

        // A withdrawal that goes all the way to settlement — reserve leaves a real line, settle does not.
        await using (var ctx = Context())
            await ((IWithdrawalLedger)Poster(ctx)).ReserveAsync(
                new ReserveWithdrawalRequest(settledWithdrawalId, Merchant, Asset, WithdrawAmount, BigInteger.Zero), Ct);
        await using (var ctx = Context())
            await Poster(ctx).SettleWithdrawalAsync(
                new SettleWithdrawalCommand(settledWithdrawalId, Merchant, Asset, WithdrawAmount, BigInteger.Zero), Ct);

        // A withdrawal that gets rejected — reserve then release, both real lines.
        await using (var ctx = Context())
            await ((IWithdrawalLedger)Poster(ctx)).ReserveAsync(
                new ReserveWithdrawalRequest(releasedWithdrawalId, Merchant, Asset, WithdrawAmount, BigInteger.Zero), Ct);
        await using (var ctx = Context())
            await Poster(ctx).ReleaseWithdrawalAsync(
                new ReleaseWithdrawalCommand(releasedWithdrawalId, Merchant, Asset, WithdrawAmount, BigInteger.Zero), Ct);

        // Manual credit + debit.
        await using (var ctx = Context())
            await Poster(ctx).CreditMerchantBalanceAsync(new CreditMerchantBalanceCommand(Merchant, Asset, WithdrawAmount, "test credit"), Ct);
        await using (var ctx = Context())
            await Poster(ctx).DebitMerchantBalanceAsync(new DebitMerchantBalanceCommand(Merchant, Asset, WithdrawAmount, "test debit"), Ct);

        await using var verify = Context();
        var (items, total) = await Query(verify).GetMerchantBalanceHistoryAsync(Merchant, null, null, null, 1, 50, Ct);

        total.ShouldBe(6); // Deposit, Reserve x2, Release, Adjustment(credit), Adjustment(debit) — NOT Settle
        items.Count.ShouldBe(6);
        items.ShouldAllBe(i => i.ReferenceType != "WithdrawalSettle");
        items.Select(i => i.ReferenceType).ShouldContain("Deposit");
        items.Select(i => i.ReferenceType).ShouldContain("WithdrawalRelease");
        items.Count(i => i.ReferenceType == "WithdrawalReserve").ShouldBe(2);
        items.Count(i => i.ReferenceType == "Adjustment").ShouldBe(2);

        // Newest first.
        items.ShouldBe(items.OrderByDescending(i => i.CreatedAt), ignoreOrder: false);
    }

    [Fact]
    public async Task History_is_scoped_to_one_asset_when_assetId_is_supplied()
    {
        var otherAsset = Guid.CreateVersion7();

        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Deposited), Ct);
        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, otherAsset, Deposited), Ct);

        await using var verify = Context();
        var (items, total) = await Query(verify).GetMerchantBalanceHistoryAsync(Merchant, Asset, null, null, 1, 50, Ct);

        total.ShouldBe(1);
        items.Single().AssetId.ShouldBe(Asset);
    }

    [Fact]
    public async Task A_merchant_with_no_history_gets_an_empty_page_not_an_error()
    {
        await using var ctx = Context();
        var (items, total) = await Query(ctx).GetMerchantBalanceHistoryAsync(Guid.CreateVersion7(), null, null, null, 1, 50, Ct);

        total.ShouldBe(0);
        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task BalanceAfter_steps_correctly_through_a_sequence_of_entries()
    {
        var merchant = Guid.CreateVersion7();
        var withdrawalId = Guid.CreateVersion7();
        var reserveAmount = BigInteger.Parse("30000000");
        var manualCredit = BigInteger.Parse("20000000");
        var manualDebit = BigInteger.Parse("10000000");

        // Oldest -> newest: +100 (deposit), -30 (reserve), +20 (manual credit), -10 (manual debit) => 100, 70, 90, 80.
        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), merchant, Asset, Deposited), Ct);
        await using (var ctx = Context())
            await ((IWithdrawalLedger)Poster(ctx)).ReserveAsync(
                new ReserveWithdrawalRequest(withdrawalId, merchant, Asset, reserveAmount, BigInteger.Zero), Ct);
        await using (var ctx = Context())
            await Poster(ctx).CreditMerchantBalanceAsync(new CreditMerchantBalanceCommand(merchant, Asset, manualCredit, "credit"), Ct);
        await using (var ctx = Context())
            await Poster(ctx).DebitMerchantBalanceAsync(new DebitMerchantBalanceCommand(merchant, Asset, manualDebit, "debit"), Ct);

        await using var verify = Context();
        var (items, _) = await Query(verify).GetMerchantBalanceHistoryAsync(merchant, null, null, null, 1, 50, Ct);

        // Newest first: debit(80), credit(90), reserve(70), deposit(100).
        items.Select(i => i.BalanceAfter).ShouldBe([
            BigInteger.Parse("80000000"),
            BigInteger.Parse("90000000"),
            BigInteger.Parse("70000000"),
            BigInteger.Parse("100000000"),
        ]);
    }

    [Fact]
    public async Task BalanceAfter_stays_correct_on_a_page_that_is_not_the_first()
    {
        var merchant = Guid.CreateVersion7();
        var withdrawalId = Guid.CreateVersion7();
        var reserveAmount = BigInteger.Parse("30000000");
        var manualCredit = BigInteger.Parse("20000000");
        var manualDebit = BigInteger.Parse("10000000");

        // Same sequence as above: 100, 70, 90, 80 (oldest -> newest).
        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), merchant, Asset, Deposited), Ct);
        await using (var ctx = Context())
            await ((IWithdrawalLedger)Poster(ctx)).ReserveAsync(
                new ReserveWithdrawalRequest(withdrawalId, merchant, Asset, reserveAmount, BigInteger.Zero), Ct);
        await using (var ctx = Context())
            await Poster(ctx).CreditMerchantBalanceAsync(new CreditMerchantBalanceCommand(merchant, Asset, manualCredit, "credit"), Ct);
        await using (var ctx = Context())
            await Poster(ctx).DebitMerchantBalanceAsync(new DebitMerchantBalanceCommand(merchant, Asset, manualDebit, "debit"), Ct);

        await using var verify = Context();

        // Page 2 of 2 (pageSize 2) holds the two OLDEST entries — the case that would break a naive
        // "always start from the current balance" implementation, since page 1 sits between these rows and
        // "now".
        var (page2, _) = await Query(verify).GetMerchantBalanceHistoryAsync(merchant, null, null, null, 2, 2, Ct);

        page2.Select(i => i.BalanceAfter).ShouldBe([
            BigInteger.Parse("70000000"),  // reserve
            BigInteger.Parse("100000000"), // deposit
        ]);
    }

    [Fact]
    public async Task BalanceAfter_tracks_each_asset_independently_when_interleaved()
    {
        var merchant = Guid.CreateVersion7();
        var assetA = Guid.CreateVersion7();
        var assetB = Guid.CreateVersion7();

        // Interleaved oldest -> newest: A+100, B+50, A+10 (credit), B-5 (debit).
        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), merchant, assetA, Deposited), Ct);
        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), merchant, assetB, BigInteger.Parse("50000000")), Ct);
        await using (var ctx = Context())
            await Poster(ctx).CreditMerchantBalanceAsync(new CreditMerchantBalanceCommand(merchant, assetA, BigInteger.Parse("10000000"), "credit"), Ct);
        await using (var ctx = Context())
            await Poster(ctx).DebitMerchantBalanceAsync(new DebitMerchantBalanceCommand(merchant, assetB, BigInteger.Parse("5000000"), "debit"), Ct);

        await using var verify = Context();
        var (items, _) = await Query(verify).GetMerchantBalanceHistoryAsync(merchant, null, null, null, 1, 50, Ct);

        // Neither asset's running total is ever perturbed by the other asset's entries.
        items.Where(i => i.AssetId == assetA).Select(i => i.BalanceAfter)
            .ShouldBe([BigInteger.Parse("110000000"), BigInteger.Parse("100000000")]);
        items.Where(i => i.AssetId == assetB).Select(i => i.BalanceAfter)
            .ShouldBe([BigInteger.Parse("45000000"), BigInteger.Parse("50000000")]);
    }
}
