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

public sealed class LedgerWithdrawalTests : LedgerTestHost
{
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly BigInteger Deposited = BigInteger.Parse("10000000"); // 10 USDT
    private static readonly BigInteger Amount = BigInteger.Parse("3000000");     // withdraw 3
    private static readonly BigInteger Fee = BigInteger.Parse("100000");         // fee 0.1
    private static BigInteger Total => Amount + Fee;

    private static async Task<BigInteger> BalanceAsync(LedgerDbContext ctx, AccountType type, Guid? ownerId) =>
        (await ctx.AccountBalances
            .Join(ctx.Accounts, b => b.Id, a => a.Id, (b, a) => new { b, a })
            .Where(x => x.a.AccountType == type && x.a.OwnerId == ownerId && x.a.AssetId == Asset)
            .Select(x => x.b.Balance)
            .SingleOrDefaultAsync(Ct));

    private async Task SeedMerchantBalanceAsync()
    {
        await using var ctx = Context();
        (await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Deposited), Ct))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Reserve_debits_the_merchant_and_holds_the_funds_in_clearing()
    {
        await SeedMerchantBalanceAsync();
        var withdrawalId = Guid.CreateVersion7();

        await using (var ctx = Context())
            (await ((IWithdrawalLedger)Poster(ctx)).ReserveAsync(new ReserveWithdrawalRequest(withdrawalId, Merchant, Asset, Amount, Fee), Ct))
                .IsSuccess.ShouldBeTrue();

        await using (var verify = Context())
        {
            (await BalanceAsync(verify, AccountType.MerchantLiability, Merchant)).ShouldBe(Deposited - Total);
            (await BalanceAsync(verify, AccountType.WithdrawalClearing, null)).ShouldBe(Total);
        }
    }

    [Fact]
    public async Task Reserve_is_refused_when_the_merchant_cannot_afford_it()
    {
        // No deposit seeded → zero balance → the negative guard rejects the reserve as InsufficientBalance.
        await using var ctx = Context();
        var result = await ((IWithdrawalLedger)Poster(ctx))
            .ReserveAsync(new ReserveWithdrawalRequest(Guid.CreateVersion7(), Merchant, Asset, Amount, Fee), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(LedgerErrors.InsufficientBalance.Code);
    }

    [Fact]
    public async Task Two_concurrent_reserves_cannot_both_drain_the_balance()
    {
        // Seed exactly enough for ONE withdrawal of Total; two reserves compete, only one may win.
        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, Total), Ct);

        async Task<bool> ReserveAsync()
        {
            await using var ctx = Context();
            var r = await ((IWithdrawalLedger)Poster(ctx))
                .ReserveAsync(new ReserveWithdrawalRequest(Guid.CreateVersion7(), Merchant, Asset, Amount, Fee), Ct);
            return r.IsSuccess;
        }

        var results = await Task.WhenAll(ReserveAsync(), ReserveAsync());

        results.Count(ok => ok).ShouldBe(1); // exactly one succeeded
        await using var verify = Context();
        (await BalanceAsync(verify, AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Zero);
    }

    [Fact]
    public async Task Settle_moves_the_amount_out_of_custody_and_keeps_the_fee_as_revenue()
    {
        await SeedMerchantBalanceAsync();
        var withdrawalId = Guid.CreateVersion7();

        await using (var ctx = Context())
            await ((IWithdrawalLedger)Poster(ctx)).ReserveAsync(new ReserveWithdrawalRequest(withdrawalId, Merchant, Asset, Amount, Fee), Ct);

        await using (var ctx = Context())
            (await Poster(ctx).SettleWithdrawalAsync(new SettleWithdrawalCommand(withdrawalId, Merchant, Asset, Amount, Fee), Ct))
                .Value.ShouldBe(PostingOutcome.Posted);

        await using (var verify = Context())
        {
            (await BalanceAsync(verify, AccountType.WithdrawalClearing, null)).ShouldBe(BigInteger.Zero);   // cleared
            (await BalanceAsync(verify, AccountType.TreasuryAsset, null)).ShouldBe(Deposited - Amount);      // amount left custody
            (await BalanceAsync(verify, AccountType.FeeRevenue, null)).ShouldBe(Fee);                        // fee retained as revenue
            (await BalanceAsync(verify, AccountType.MerchantLiability, Merchant)).ShouldBe(Deposited - Total); // stays debited
        }
    }

    [Fact]
    public async Task Release_returns_the_reserved_funds_to_the_merchant()
    {
        await SeedMerchantBalanceAsync();
        var withdrawalId = Guid.CreateVersion7();

        await using (var ctx = Context())
            await ((IWithdrawalLedger)Poster(ctx)).ReserveAsync(new ReserveWithdrawalRequest(withdrawalId, Merchant, Asset, Amount, Fee), Ct);

        await using (var ctx = Context())
            (await Poster(ctx).ReleaseWithdrawalAsync(new ReleaseWithdrawalCommand(withdrawalId, Merchant, Asset, Amount, Fee), Ct))
                .Value.ShouldBe(PostingOutcome.Posted);

        await using (var verify = Context())
        {
            (await BalanceAsync(verify, AccountType.WithdrawalClearing, null)).ShouldBe(BigInteger.Zero);
            (await BalanceAsync(verify, AccountType.MerchantLiability, Merchant)).ShouldBe(Deposited); // fully restored
        }
    }

    [Fact]
    public async Task A_replayed_reserve_does_not_debit_twice()
    {
        await SeedMerchantBalanceAsync();
        var withdrawalId = Guid.CreateVersion7();
        var request = new ReserveWithdrawalRequest(withdrawalId, Merchant, Asset, Amount, Fee);

        await using (var ctx = Context())
            (await ((IWithdrawalLedger)Poster(ctx)).ReserveAsync(request, Ct)).IsSuccess.ShouldBeTrue();
        await using (var ctx = Context())
            (await ((IWithdrawalLedger)Poster(ctx)).ReserveAsync(request, Ct)).IsSuccess.ShouldBeTrue(); // replay → no-op

        await using var verify = Context();
        (await verify.Journals.CountAsync(j => j.ReferenceId == withdrawalId, Ct)).ShouldBe(1);       // one reserve journal
        (await BalanceAsync(verify, AccountType.MerchantLiability, Merchant)).ShouldBe(Deposited - Total); // debited once
    }
}
