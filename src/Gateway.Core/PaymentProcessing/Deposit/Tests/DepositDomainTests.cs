using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;
using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;
using DepositEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain.Deposit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Tests;

public sealed class DepositDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Wallet = Guid.CreateVersion7();
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly BigInteger Amount = BigInteger.Parse("5000000");

    private static readonly DepositPolicy ThreeConfs = new(CreditStrategy.Confirmations, 3, BigInteger.Parse("1000"));
    private static readonly DepositPolicy Finality = new(CreditStrategy.Finalized, 0, BigInteger.Zero);

    private static DepositEntity Record(BigInteger amount, DepositPolicy policy) =>
        DepositEntity.Record(Chain.Tron, "TAddr", Wallet, Merchant, Asset, amount, "0xtx", 0, 100, "hash100", policy, Now).Value;

    [Fact]
    public void An_amount_at_or_above_the_minimum_is_detected()
    {
        var deposit = Record(Amount, ThreeConfs);
        deposit.Status.ShouldBe(DepositStatus.Detected);
        deposit.MerchantId.ShouldBe(Merchant);
        deposit.Amount.ShouldBe(Amount);
    }

    [Fact]
    public void Dust_below_the_minimum_is_recorded_as_ignored_never_credited()
    {
        var deposit = Record(BigInteger.Parse("999"), ThreeConfs);
        deposit.Status.ShouldBe(DepositStatus.Ignored);
    }

    [Theory]
    [InlineData("", "0xtx", "deposit.address_required")]
    [InlineData("TAddr", "", "deposit.tx_hash_required")]
    public void Record_validates_required_fields(string address, string txHash, string expectedCode) =>
        DepositEntity.Record(Chain.Tron, address, Wallet, Merchant, Asset, Amount, txHash, 0, 100, "h", ThreeConfs, Now)
            .Error!.Code.ShouldBe(expectedCode);

    [Fact]
    public void Record_rejects_a_non_positive_amount() =>
        DepositEntity.Record(Chain.Tron, "TAddr", Wallet, Merchant, Asset, BigInteger.Zero, "0xtx", 0, 100, "h", ThreeConfs, Now)
            .Error!.Code.ShouldBe(DepositErrors.AmountNotPositive.Code);

    [Fact]
    public void Record_rejects_a_missing_owner() =>
        DepositEntity.Record(Chain.Tron, "TAddr", Guid.Empty, Merchant, Asset, Amount, "0xtx", 0, 100, "h", ThreeConfs, Now)
            .Error!.Code.ShouldBe(DepositErrors.OwnerRequired.Code);

    [Fact]
    public void Below_the_threshold_it_stays_detected_and_raises_nothing()
    {
        var deposit = Record(Amount, ThreeConfs);

        deposit.RegisterConfirmations(confirmations: 2, isFinalized: false, ThreeConfs, Now);

        deposit.Status.ShouldBe(DepositStatus.Detected);
        deposit.Confirmations.ShouldBe(2);
        deposit.DomainEvents.OfType<DepositConfirmed>().ShouldBeEmpty();
    }

    [Fact]
    public void At_the_threshold_it_confirms_and_raises_deposit_confirmed_once()
    {
        var deposit = Record(Amount, ThreeConfs);

        deposit.RegisterConfirmations(confirmations: 3, isFinalized: false, ThreeConfs, Now);

        deposit.Status.ShouldBe(DepositStatus.Confirmed);
        deposit.ConfirmedAt.ShouldBe(Now);

        var evt = deposit.DomainEvents.OfType<DepositConfirmed>().ShouldHaveSingleItem();
        evt.DepositId.ShouldBe(deposit.Id);
        evt.MerchantId.ShouldBe(Merchant);
        evt.AssetId.ShouldBe(Asset);
        evt.AmountBaseUnits.ShouldBe(Amount.ToString());

        // Idempotent: another pass does not re-confirm or re-raise.
        deposit.RegisterConfirmations(confirmations: 10, isFinalized: true, ThreeConfs, Now.AddMinutes(1));
        deposit.DomainEvents.OfType<DepositConfirmed>().Count().ShouldBe(1);
    }

    [Fact]
    public void A_finality_chain_confirms_on_the_finalized_signal_not_a_count()
    {
        var deposit = Record(Amount, Finality);

        deposit.RegisterConfirmations(confirmations: 0, isFinalized: false, Finality, Now);
        deposit.Status.ShouldBe(DepositStatus.Detected);

        deposit.RegisterConfirmations(confirmations: 0, isFinalized: true, Finality, Now);
        deposit.Status.ShouldBe(DepositStatus.Confirmed);
        deposit.DomainEvents.OfType<DepositConfirmed>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Orphaning_a_still_pending_deposit_raises_no_reversal()
    {
        var deposit = Record(Amount, ThreeConfs); // Detected, never credited

        deposit.MarkOrphaned(Now);

        deposit.Status.ShouldBe(DepositStatus.Orphaned);
        deposit.DomainEvents.OfType<DepositOrphaned>().ShouldBeEmpty(); // nothing was credited to reverse
    }

    [Fact]
    public void Orphaning_a_confirmed_deposit_raises_a_reversal()
    {
        var deposit = Record(Amount, ThreeConfs);
        deposit.RegisterConfirmations(3, false, ThreeConfs, Now); // Confirmed (credited)

        deposit.MarkOrphaned(Now.AddMinutes(1));

        deposit.Status.ShouldBe(DepositStatus.Orphaned);
        var evt = deposit.DomainEvents.OfType<DepositOrphaned>().ShouldHaveSingleItem();
        evt.DepositId.ShouldBe(deposit.Id);
        evt.AmountBaseUnits.ShouldBe(Amount.ToString());
    }

    [Fact]
    public void Orphaning_is_idempotent()
    {
        var deposit = Record(Amount, ThreeConfs);
        deposit.RegisterConfirmations(3, false, ThreeConfs, Now);

        deposit.MarkOrphaned(Now);
        deposit.MarkOrphaned(Now.AddMinutes(1));

        deposit.DomainEvents.OfType<DepositOrphaned>().Count().ShouldBe(1);
    }
}
