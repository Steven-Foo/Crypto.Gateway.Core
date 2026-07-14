using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Tests;

public sealed class JournalDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly Guid Treasury = Guid.CreateVersion7();
    private static readonly Guid Liability = Guid.CreateVersion7();
    private static readonly Guid Deposit = Guid.CreateVersion7();
    private static readonly Guid Merchant = Guid.CreateVersion7();

    private static PostingLine[] BalancedDeposit(BigInteger amount) =>
    [
        PostingLine.Debit(Treasury, amount),
        PostingLine.Credit(Liability, amount),
    ];

    [Fact]
    public void A_balanced_deposit_journal_is_constructed_with_its_two_immutable_entries()
    {
        var amount = BigInteger.Parse("1000000"); // 1 USDT in sun-scale base units

        var journal = Journal.Post(
            JournalReferenceType.Deposit, Deposit, Asset, Merchant, "Deposit credit", BalancedDeposit(amount), Now).Value;

        journal.AssetId.ShouldBe(Asset);
        journal.MerchantId.ShouldBe(Merchant);
        journal.Entries.Count.ShouldBe(2);

        var debit = journal.Entries.Single(e => e.IsDebit);
        var credit = journal.Entries.Single(e => !e.IsDebit);

        debit.AccountId.ShouldBe(Treasury);
        debit.Debit.ShouldBe(amount);
        debit.Credit.ShouldBe(BigInteger.Zero);

        credit.AccountId.ShouldBe(Liability);
        credit.Credit.ShouldBe(amount);
        credit.Debit.ShouldBe(BigInteger.Zero);

        // Every line inherits the journal's single asset.
        journal.Entries.ShouldAllBe(e => e.AssetId == Asset);
    }

    [Fact]
    public void An_unbalanced_journal_is_unconstructable()
    {
        PostingLine[] lines =
        [
            PostingLine.Debit(Treasury, 1000),
            PostingLine.Credit(Liability, 999),
        ];

        Journal.Post(JournalReferenceType.Deposit, Deposit, Asset, Merchant, "x", lines, Now)
            .Error!.Code.ShouldBe(LedgerErrors.Unbalanced.Code);
    }

    [Fact]
    public void A_single_line_journal_is_unconstructable()
    {
        PostingLine[] lines = [PostingLine.Debit(Treasury, 1000)];

        Journal.Post(JournalReferenceType.Deposit, Deposit, Asset, Merchant, "x", lines, Now)
            .Error!.Code.ShouldBe(LedgerErrors.JournalNeedsTwoLines.Code);
    }

    [Fact]
    public void A_zero_amount_line_is_rejected()
    {
        PostingLine[] lines =
        [
            PostingLine.Debit(Treasury, 0),
            PostingLine.Credit(Liability, 0),
        ];

        Journal.Post(JournalReferenceType.Deposit, Deposit, Asset, Merchant, "x", lines, Now)
            .Error!.Code.ShouldBe(LedgerErrors.NonPositiveAmount.Code);
    }

    [Fact]
    public void A_negative_amount_line_is_rejected()
    {
        PostingLine[] lines =
        [
            PostingLine.Debit(Treasury, -5),
            PostingLine.Credit(Liability, -5),
        ];

        Journal.Post(JournalReferenceType.Deposit, Deposit, Asset, Merchant, "x", lines, Now)
            .Error!.Code.ShouldBe(LedgerErrors.NonPositiveAmount.Code);
    }

    [Fact]
    public void An_amount_beyond_the_38_digit_storage_limit_is_rejected_not_truncated()
    {
        var tooBig = BigInteger.Pow(10, 38); // 39 digits — one past the limit
        PostingLine[] lines =
        [
            PostingLine.Debit(Treasury, tooBig),
            PostingLine.Credit(Liability, tooBig),
        ];

        Journal.Post(JournalReferenceType.Deposit, Deposit, Asset, Merchant, "x", lines, Now)
            .Error!.Code.ShouldBe(LedgerErrors.NonPositiveAmount.Code);
    }

    [Fact]
    public void A_journal_without_an_asset_is_rejected() =>
        Journal.Post(JournalReferenceType.Deposit, Deposit, Guid.Empty, Merchant, "x", BalancedDeposit(1000), Now)
            .Error!.Code.ShouldBe(LedgerErrors.AssetRequired.Code);

    [Fact]
    public void A_journal_without_a_business_reference_is_rejected() =>
        Journal.Post(JournalReferenceType.Deposit, Guid.Empty, Asset, Merchant, "x", BalancedDeposit(1000), Now)
            .Error!.Code.ShouldBe(LedgerErrors.ReferenceRequired.Code);

    [Fact]
    public void A_line_without_an_account_is_rejected()
    {
        PostingLine[] lines =
        [
            PostingLine.Debit(Guid.Empty, 1000),
            PostingLine.Credit(Liability, 1000),
        ];

        Journal.Post(JournalReferenceType.Deposit, Deposit, Asset, Merchant, "x", lines, Now)
            .Error!.Code.ShouldBe(LedgerErrors.AccountRequired.Code);
    }

    [Fact]
    public void A_platform_internal_journal_may_have_no_merchant()
    {
        var journal = Journal.Post(
            JournalReferenceType.Sweep, Guid.CreateVersion7(), Asset, merchantId: null, "Sweep", BalancedDeposit(1000), Now).Value;

        journal.MerchantId.ShouldBeNull();
    }

    [Fact]
    public void A_multi_line_journal_balances_on_totals_not_pairwise()
    {
        // Deposit split into a merchant credit and a fee: debits still equal credits overall.
        var gross = new BigInteger(1000);
        var fee = new BigInteger(30);
        var net = gross - fee;

        PostingLine[] lines =
        [
            PostingLine.Debit(Treasury, gross),
            PostingLine.Credit(Liability, net),
            PostingLine.Credit(Deposit /* reuse guid as a fee-revenue account id */, fee),
        ];

        var journal = Journal.Post(JournalReferenceType.Deposit, Guid.CreateVersion7(), Asset, Merchant, "Deposit less fee", lines, Now);

        journal.IsSuccess.ShouldBeTrue();
        journal.Value.Entries.Count.ShouldBe(3);
    }
}
