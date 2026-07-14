using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Tests;

public sealed class AccountBalanceDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly Guid Treasury = Guid.CreateVersion7();
    private static readonly Guid Liability = Guid.CreateVersion7();

    /// <summary>Builds a real journal so we fold genuine <see cref="JournalEntry"/> instances, not fakes.</summary>
    private static (JournalEntry debit, JournalEntry credit) EntriesFor(BigInteger amount)
    {
        var journal = Journal.Post(
            JournalReferenceType.Deposit, Guid.CreateVersion7(), Asset, Guid.CreateVersion7(), "x",
            [PostingLine.Debit(Treasury, amount), PostingLine.Credit(Liability, amount)], Now).Value;

        return (journal.Entries.Single(e => e.IsDebit), journal.Entries.Single(e => !e.IsDebit));
    }

    [Fact]
    public void A_credit_raises_a_credit_normal_liability_balance()
    {
        var (_, credit) = EntriesFor(1000);
        var balance = AccountBalance.Open(Liability, Now);

        balance.Apply(NormalSide.Credit, credit, Now).IsSuccess.ShouldBeTrue();

        balance.Balance.ShouldBe(new BigInteger(1000));
        balance.LastEntryId.ShouldBe(credit.Id);
    }

    [Fact]
    public void A_debit_raises_a_debit_normal_treasury_balance()
    {
        var (debit, _) = EntriesFor(1000);
        var balance = AccountBalance.Open(Treasury, Now);

        balance.Apply(NormalSide.Debit, debit, Now).IsSuccess.ShouldBeTrue();

        balance.Balance.ShouldBe(new BigInteger(1000));
    }

    [Fact]
    public void A_reversal_nets_a_liability_balance_back_to_zero()
    {
        var (_, credit) = EntriesFor(1000);
        var (reversalDebit, _) = EntriesFor(1000); // the compensating journal debits the liability

        var balance = AccountBalance.Open(Liability, Now);
        balance.Apply(NormalSide.Credit, credit, Now);

        balance.Apply(NormalSide.Credit, reversalDebit, Now).IsSuccess.ShouldBeTrue();

        // credit +1000 then a debit against a credit-normal account -1000 == 0
        balance.Balance.ShouldBe(BigInteger.Zero);
    }

    [Fact]
    public void A_posting_that_would_drive_the_balance_negative_is_refused()
    {
        var (debit, _) = EntriesFor(1000); // a debit against a credit-normal liability => -1000
        var balance = AccountBalance.Open(Liability, Now);

        balance.Apply(NormalSide.Credit, debit, Now)
            .Error!.Code.ShouldBe(LedgerErrors.BalanceWouldGoNegative.Code);

        balance.Balance.ShouldBe(BigInteger.Zero); // unchanged
    }
}
