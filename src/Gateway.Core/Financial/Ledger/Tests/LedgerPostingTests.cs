using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Tests;

public sealed class LedgerPostingTests : LedgerTestHost
{
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly BigInteger Amount = BigInteger.Parse("1000000"); // 1 USDT-TRC20 (6 dp) in base units

    private static async Task<BigInteger> BalanceAsync(LedgerDbContext ctx, AccountType type, Guid? ownerId) =>
        (await ctx.AccountBalances
            .Join(ctx.Accounts, b => b.Id, a => a.Id, (b, a) => new { b, a })
            .Where(x => x.a.AccountType == type && x.a.OwnerId == ownerId && x.a.AssetId == Asset)
            .Select(x => x.b)
            .SingleAsync(Ct)).Balance;

    [Fact]
    public async Task Crediting_a_deposit_debits_treasury_and_credits_the_merchant()
    {
        var merchant = Guid.CreateVersion7();
        var deposit = Guid.CreateVersion7();

        await using (var ctx = Context())
        {
            var outcome = await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(deposit, merchant, Asset, Amount), Ct);
            outcome.Value.ShouldBe(PostingOutcome.Posted);
        }

        await using (var ctx = Context())
        {
            var journal = await ctx.Journals.Include(j => j.Entries).SingleAsync(Ct);
            journal.ReferenceType.ShouldBe(JournalReferenceType.Deposit);
            journal.ReferenceId.ShouldBe(deposit);
            journal.MerchantId.ShouldBe(merchant); // reporting dimension set
            journal.Entries.Count.ShouldBe(2);
            journal.Entries.Sum(e => (long)e.Debit).ShouldBe(journal.Entries.Sum(e => (long)e.Credit)); // balanced

            (await BalanceAsync(ctx, AccountType.MerchantLiability, merchant)).ShouldBe(Amount); // we owe the merchant
            (await BalanceAsync(ctx, AccountType.TreasuryAsset, null)).ShouldBe(Amount);          // custody rose
        }
    }

    [Fact]
    public async Task A_redelivered_deposit_is_credited_exactly_once()
    {
        var merchant = Guid.CreateVersion7();
        var deposit = Guid.CreateVersion7();
        var command = new CreditDepositCommand(deposit, merchant, Asset, Amount);

        await using (var ctx = Context())
            (await Poster(ctx).CreditDepositAsync(command, Ct)).Value.ShouldBe(PostingOutcome.Posted);

        await using (var ctx = Context())
            (await Poster(ctx).CreditDepositAsync(command, Ct)).Value.ShouldBe(PostingOutcome.AlreadyPosted);

        await using (var verify = Context())
        {
            (await verify.Journals.CountAsync(Ct)).ShouldBe(1);            // one journal, not two
            (await BalanceAsync(verify, AccountType.MerchantLiability, merchant)).ShouldBe(Amount); // credited once
        }
    }

    [Fact]
    public async Task A_reversal_nets_the_merchant_balance_back_to_zero_without_editing_history()
    {
        var merchant = Guid.CreateVersion7();
        var deposit = Guid.CreateVersion7();

        await using (var ctx = Context())
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(deposit, merchant, Asset, Amount), Ct);

        await using (var ctx = Context())
            (await Poster(ctx).ReverseDepositAsync(new ReverseDepositCommand(deposit, merchant, Asset, Amount), Ct))
                .Value.ShouldBe(PostingOutcome.Posted);

        await using (var verify = Context())
        {
            (await verify.Journals.CountAsync(Ct)).ShouldBe(2); // original + compensating, both preserved
            (await BalanceAsync(verify, AccountType.MerchantLiability, merchant)).ShouldBe(BigInteger.Zero);
            (await BalanceAsync(verify, AccountType.TreasuryAsset, null)).ShouldBe(BigInteger.Zero);
        }
    }

    [Fact]
    public async Task A_reversal_that_would_drive_the_balance_negative_is_refused()
    {
        // Reversing a deposit that was never credited would push the liability below zero — the exact
        // shape of "funds already withdrawn before a reorg orphaned the deposit". Must halt, not corrupt.
        var merchant = Guid.CreateVersion7();
        var deposit = Guid.CreateVersion7();

        await using var ctx = Context();
        var ex = await Should.ThrowAsync<LedgerPostingException>(() =>
            Poster(ctx).ReverseDepositAsync(new ReverseDepositCommand(deposit, merchant, Asset, Amount), Ct));

        ex.Error.Code.ShouldBe(LedgerErrors.BalanceWouldGoNegative.Code);
    }

    [Fact]
    public async Task The_cached_balance_equals_the_sum_of_the_journal_entries()
    {
        var merchant = Guid.CreateVersion7();

        for (var i = 0; i < 5; i++)
        {
            await using var ctx = Context();
            await Poster(ctx).CreditDepositAsync(
                new CreditDepositCommand(Guid.CreateVersion7(), merchant, Asset, Amount), Ct);
        }

        await using (var verify = Context())
        {
            var account = await verify.Accounts.SingleAsync(
                a => a.AccountType == AccountType.MerchantLiability && a.OwnerId == merchant, Ct);

            // Rebuild truth straight from the immutable lines: credit-normal => ΣCredit − ΣDebit.
            var entries = await verify.JournalEntries.Where(e => e.AccountId == account.Id).ToListAsync(Ct);
            var rebuilt = entries.Aggregate(BigInteger.Zero, (sum, e) => sum + e.Credit - e.Debit);

            var cached = (await verify.AccountBalances.SingleAsync(b => b.Id == account.Id, Ct)).Balance;

            cached.ShouldBe(rebuilt);
            cached.ShouldBe(Amount * 5);
        }
    }

    [Fact]
    public async Task Concurrent_credits_to_one_merchant_account_never_lose_an_update()
    {
        // 16 distinct deposits credit the SAME merchant liability account at once. With the Redis lock
        // disabled (see LedgerTestHost), only the AccountBalance rowversion + retry stops a lost update.
        const int n = 16;
        var merchant = Guid.CreateVersion7();

        var tasks = Enumerable.Range(0, n).Select(async _ =>
        {
            await using var ctx = Context();
            await Poster(ctx).CreditDepositAsync(
                new CreditDepositCommand(Guid.CreateVersion7(), merchant, Asset, Amount), Ct);
        });

        await Task.WhenAll(tasks);

        await using var verify = Context();
        (await verify.Journals.CountAsync(Ct)).ShouldBe(n);                       // every deposit posted
        (await BalanceAsync(verify, AccountType.MerchantLiability, merchant)).ShouldBe(Amount * n); // nothing lost
        (await BalanceAsync(verify, AccountType.TreasuryAsset, null)).ShouldBe(Amount * n);
    }

    [Fact]
    public async Task Only_one_treasury_account_exists_per_asset_across_concurrent_first_postings()
    {
        // Two merchants deposit the same asset simultaneously; both need the shared treasury account.
        // UX_Account_Natural (unfiltered) must resolve the create race to a single treasury row.
        var merchants = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };

        await Task.WhenAll(merchants.Select(async merchant =>
        {
            await using var ctx = Context();
            await Poster(ctx).CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), merchant, Asset, Amount), Ct);
        }));

        await using var verify = Context();
        (await verify.Accounts.CountAsync(a => a.AccountType == AccountType.TreasuryAsset && a.AssetId == Asset, Ct))
            .ShouldBe(1);
        (await BalanceAsync(verify, AccountType.TreasuryAsset, null)).ShouldBe(Amount * 2);
    }

    // ── Database-level invariants (defence in depth) ──────────────────────────────

    [Fact]
    public async Task The_database_rejects_a_line_that_is_both_a_debit_and_a_credit()
    {
        var journalId = Guid.CreateVersion7();
        await using var ctx = Context();

        // Seed a journal row so the FK holds, then try to insert a both-sided entry via raw SQL.
        await ctx.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ledger.Journal (Id, ReferenceType, ReferenceId, AssetId, Description, CreatedAt)
            VALUES ({0}, 'Adjustment', {1}, {2}, 'x', SYSDATETIMEOFFSET())
            """,
            [journalId, Guid.CreateVersion7(), Asset], Ct);

        var ex = await Should.ThrowAsync<SqlException>(() => ctx.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ledger.JournalEntry (Id, JournalId, AccountId, AssetId, Debit, Credit, CreatedAt)
            VALUES (NEWID(), {0}, NEWID(), {1}, 5, 5, SYSDATETIMEOFFSET())
            """,
            [journalId, Asset], Ct));

        ex.Message.ShouldContain("CK_JournalEntry_DebitXorCredit");
    }

    [Fact]
    public async Task The_database_rejects_a_second_journal_for_the_same_business_event()
    {
        var reference = Guid.CreateVersion7();
        await using var ctx = Context();

        await ctx.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ledger.Journal (Id, ReferenceType, ReferenceId, AssetId, Description, CreatedAt)
            VALUES (NEWID(), 'Deposit', {0}, {1}, 'first', SYSDATETIMEOFFSET())
            """,
            [reference, Asset], Ct);

        var ex = await Should.ThrowAsync<SqlException>(() => ctx.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ledger.Journal (Id, ReferenceType, ReferenceId, AssetId, Description, CreatedAt)
            VALUES (NEWID(), 'Deposit', {0}, {1}, 'dup', SYSDATETIMEOFFSET())
            """,
            [reference, Asset], Ct));

        ex.Message.ShouldContain("UX_Journal_Reference");
    }
}
