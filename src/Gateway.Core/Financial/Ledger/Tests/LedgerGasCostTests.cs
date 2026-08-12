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
/// 5c platform gas accounting: the native-coin cost the platform bears for an on-chain operation is a balanced
/// platform journal — DEBIT NetworkFeeExpense / CREDIT PlatformFunding — idempotent per operation, and a
/// zero fee writes nothing (dev's in-memory engine reports no fee).
/// </summary>
public sealed class LedgerGasCostTests : LedgerTestHost
{
    private static readonly Guid GasAsset = Guid.CreateVersion7(); // native TRX, for gas denomination
    private static readonly BigInteger Fee = BigInteger.Parse("1100000"); // 1.1 TRX in sun

    private static async Task<BigInteger> SystemBalanceAsync(LedgerDbContext ctx, AccountType type) =>
        (await ctx.AccountBalances
            .Join(ctx.Accounts, b => b.Id, a => a.Id, (b, a) => new { b, a })
            .Where(x => x.a.AccountType == type && x.a.OwnerId == null && x.a.AssetId == GasAsset)
            .Select(x => x.b.Balance)
            .SingleOrDefaultAsync(Ct));

    [Fact]
    public async Task A_gas_cost_debits_the_expense_and_credits_platform_funding_by_the_fee()
    {
        var operationId = Guid.CreateVersion7();

        await using (var ctx = Context())
            (await Poster(ctx).RecordGasSpentAsync(new RecordGasSpentCommand(operationId, "Withdrawal", GasAsset, Fee), Ct))
                .Value.ShouldBe(PostingOutcome.Posted);

        await using var verify = Context();
        (await SystemBalanceAsync(verify, AccountType.NetworkFeeExpense)).ShouldBe(Fee); // expense grew (debit-normal)
        (await SystemBalanceAsync(verify, AccountType.PlatformFunding)).ShouldBe(Fee);   // funding drawn (credit-normal)
    }

    [Fact]
    public async Task Re_recording_the_same_operation_is_idempotent()
    {
        var operationId = Guid.CreateVersion7();
        var command = new RecordGasSpentCommand(operationId, "Withdrawal", GasAsset, Fee);

        await using (var ctx = Context())
            (await Poster(ctx).RecordGasSpentAsync(command, Ct)).Value.ShouldBe(PostingOutcome.Posted);
        await using (var ctx = Context())
            (await Poster(ctx).RecordGasSpentAsync(command, Ct)).Value.ShouldBe(PostingOutcome.AlreadyPosted);

        await using var verify = Context();
        (await SystemBalanceAsync(verify, AccountType.NetworkFeeExpense)).ShouldBe(Fee); // not double-booked
    }

    [Fact]
    public async Task A_zero_fee_books_nothing()
    {
        await using var ctx = Context();
        (await Poster(ctx).RecordGasSpentAsync(new RecordGasSpentCommand(Guid.CreateVersion7(), "Withdrawal", GasAsset, BigInteger.Zero), Ct))
            .Value.ShouldBe(PostingOutcome.NoChange);

        (await SystemBalanceAsync(ctx, AccountType.NetworkFeeExpense)).ShouldBe(BigInteger.Zero);
    }
}
